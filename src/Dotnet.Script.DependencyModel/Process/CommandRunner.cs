using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
            if (startInformation.RedirectStandardOutput)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                var stdout = stdoutTask.GetAwaiter().GetResult();
                var stderr = stderrTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(stdout)) _logger.Debug(stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) _logger.Error(stderr);
            }
            else
            {
                process.WaitForExit();
            }
            return process.ExitCode;
        }

        public CommandResult Capture(string commandPath, string arguments, string workingDirectory = null)
        {
            var startInformation = CreateProcessStartInfo(commandPath, arguments, workingDirectory);
            using var process = CreateProcess(startInformation);
            process.Start();
            string stdout, stderr;
            if (startInformation.RedirectStandardOutput)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
            }
            else
            {
                process.WaitForExit();
                stdout = string.Empty;
                stderr = string.Empty;
            }
            return new CommandResult(process.ExitCode, stdout, stderr);
        }

        private static ProcessStartInfo CreateProcessStartInfo(string commandPath, string arguments, string workingDirectory)
        {
            // On Unix (macOS and Linux), anonymous pipes are backed by AF_UNIX socketpairs.
            // The .NET runtime's async socket I/O engine (SocketAsyncEngine) registers those
            // sockets in a static epoll/kqueue-based dispatcher. Even after Process.Dispose()
            // the engine may hold socket references in its static state, preventing GC
            // collection. If the same fd numbers are later recycled by a script child process
            // (e.g. CliWrap), the stale engine state corrupts the new socket →
            // EADDRNOTAVAIL (errno 49). Disabling redirection on Unix avoids creating the
            // socketpairs entirely; subprocess output goes directly to the terminal instead.
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

        private static void RemoveMsBuildEnvironmentVariables(IDictionary<string, string> environment)
        {
            // Remove various MSBuild environment variables set by OmniSharp to ensure that
            // the .NET CLI is not launched with the wrong values.
            environment.Remove("MSBUILD_EXE_PATH");
            environment.Remove("MSBuildExtensionsPath");
        }

        private static bool IsWindows()
        {
            return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
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
