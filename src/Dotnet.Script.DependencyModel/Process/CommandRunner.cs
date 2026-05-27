using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Dotnet.Script.DependencyModel.Logging;

namespace Dotnet.Script.DependencyModel.Process
{
    public class CommandRunner
    {
        private readonly Logger _logger;

        public CommandRunner(LogFactory logFactory)
        {
            _logger = logFactory.CreateLogger<CommandRunner>();
        }

        public int Execute(string commandPath, string arguments = null, string workingDirectory = null)
        {
            _logger.Debug($"Executing '{commandPath} {arguments}'");
            var startInformation = CreateProcessStartInfo(commandPath, arguments, workingDirectory);
            using var process = CreateProcess(startInformation);
            process.Start();
            var (stdout, stderr) = ReadOutputSync(process, startInformation.RedirectStandardOutput);
            if (!string.IsNullOrWhiteSpace(stdout)) _logger.Debug(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) _logger.Error(stderr);
            return process.ExitCode;
        }

        public CommandResult Capture(string commandPath, string arguments, string workingDirectory = null)
        {
            if (!IsWindows())
            {
                // On Unix, pipe redirection uses AF_UNIX socketpairs that persist in
                // SocketAsyncEngine's static state, causing EADDRNOTAVAIL when a script's
                // child process (e.g. CliWrap) later recycles the same fd numbers.
                // Run via a shell script that redirects output to temp files instead —
                // no socketpairs are created, and output is fully captured.
                return CaptureViaShell(commandPath, arguments, workingDirectory);
            }
            var startInformation = CreateProcessStartInfo(commandPath, arguments, workingDirectory);
            using var process = CreateProcess(startInformation);
            process.Start();
            var (stdout, stderr) = ReadOutputSync(process, true);
            return new CommandResult(process.ExitCode, stdout, stderr);
        }

        private static CommandResult CaptureViaShell(string commandPath, string arguments, string workingDirectory)
        {
            var tmpStdout = Path.GetTempFileName();
            var tmpStderr = Path.GetTempFileName();
            var tmpScript = Path.GetTempFileName();
            try
            {
                // Write a shell script that runs the command with file-based I/O redirection.
                // This avoids anonymous pipe socketpairs entirely — the shell forks+execs the
                // command with its stdout/stderr pointing at regular files, not AF_UNIX sockets.
                File.WriteAllText(tmpScript,
                    $"#!/bin/sh\n\"{commandPath}\" {arguments} >{tmpStdout} 2>{tmpStderr}\n");

                var startInfo = new ProcessStartInfo("/bin/sh")
                {
                    Arguments = tmpScript,
                    UseShellExecute = false,
                    WorkingDirectory = workingDirectory ?? System.Environment.CurrentDirectory
                };
                RemoveMsBuildEnvironmentVariables(startInfo.Environment);

                using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                process.Start();
                process.WaitForExit();

                return new CommandResult(
                    process.ExitCode,
                    File.ReadAllText(tmpStdout),
                    File.ReadAllText(tmpStderr));
            }
            finally
            {
                TryDelete(tmpScript);
                TryDelete(tmpStdout);
                TryDelete(tmpStderr);
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private static (string stdout, string stderr) ReadOutputSync(System.Diagnostics.Process process, bool redirect)
        {
            if (!redirect)
            {
                process.WaitForExit();
                return (string.Empty, string.Empty);
            }
            string stdout = null, stderr = null;
            var stdoutThread = new System.Threading.Thread(() => stdout = process.StandardOutput.ReadToEnd());
            var stderrThread = new System.Threading.Thread(() => stderr = process.StandardError.ReadToEnd());
            stdoutThread.Start();
            stderrThread.Start();
            process.WaitForExit();
            stdoutThread.Join();
            stderrThread.Join();
            return (stdout ?? string.Empty, stderr ?? string.Empty);
        }

        private static ProcessStartInfo CreateProcessStartInfo(string commandPath, string arguments, string workingDirectory)
        {
            // On Unix, anonymous pipes are backed by AF_UNIX socketpairs. The .NET runtime's
            // async socket I/O engine (SocketAsyncEngine) registers those fds in a static
            // epoll/kqueue dispatcher — even synchronous reads are insufficient to prevent this
            // because WaitForExit() internally drains redirected streams asynchronously in
            // .NET 6+. After Process.Dispose() the OS recycles those fd numbers; if a script
            // child process (e.g. CliWrap) reuses the same fds, the stale engine state corrupts
            // the new socket → EADDRNOTAVAIL (errno 49). Disabling redirection on Unix avoids
            // creating the socketpairs entirely. Callers suppress subprocess output noise by
            // passing quiet verbosity flags to any dotnet commands they invoke.
            bool redirect = IsWindows();
            var startInformation = new ProcessStartInfo($"{commandPath}")
            {
                CreateNoWindow = true,
                Arguments = arguments ?? "",
                RedirectStandardOutput = redirect,
                RedirectStandardError = redirect,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? System.Environment.CurrentDirectory
            };

            RemoveMsBuildEnvironmentVariables(startInformation.Environment);
            return startInformation;
        }

        private static bool IsWindows() =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        private static void RemoveMsBuildEnvironmentVariables(IDictionary<string, string> environment)
        {
            // Remove various MSBuild environment variables set by OmniSharp to ensure that
            // the .NET CLI is not launched with the wrong values.
            environment.Remove("MSBUILD_EXE_PATH");
            environment.Remove("MSBuildExtensionsPath");
        }

        private static System.Diagnostics.Process CreateProcess(ProcessStartInfo startInformation)
        {
            return new System.Diagnostics.Process { StartInfo = startInformation };
        }
    }

    public class CommandResult
    {
        public CommandResult(int exitCode, string standardOut, string standardError)
        {
            ExitCode = exitCode;
            StandardOut = standardOut;
            StandardError = standardError;
        }
        public string StandardOut { get; }
        public string StandardError { get; }
        public int ExitCode { get; }

        public CommandResult EnsureSuccessfulExitCode(int success = 0)
        {
            if (ExitCode != success)
            {
                var message = !string.IsNullOrEmpty(StandardError)
                    ? StandardError
                    : $"Command failed with exit code {ExitCode}. See console output for details.";
                throw new InvalidOperationException(message);
            }
            return this;
        }
    }

}
