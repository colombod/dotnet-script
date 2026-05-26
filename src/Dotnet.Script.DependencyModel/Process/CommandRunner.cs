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
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout)) _logger.Debug(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) _logger.Error(stderr);
            return process.ExitCode;
        }

        public CommandResult Capture(string commandPath, string arguments, string workingDirectory = null)
        {
            var startInformation = CreateProcessStartInfo(commandPath, arguments, workingDirectory);
            using var process = CreateProcess(startInformation);
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new CommandResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }

        private static ProcessStartInfo CreateProcessStartInfo(string commandPath, string arguments, string workingDirectory)
        {
            var startInformation = new ProcessStartInfo($"{commandPath}")
            {
                CreateNoWindow = true,
                Arguments = arguments ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
                throw new InvalidOperationException(StandardError);
            }
            return this;
        }
    }

}