using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.ApplicationModel.TestHost;

internal static class Program
{
    private const string DescribePidPathVariable = "COHESION_APPLICATION_MODEL_DESCRIBE_PID_PATH";
    private const string DescribeDocumentPathVariable = "COHESION_APPLICATION_MODEL_DESCRIBE_DOCUMENT_PATH";
    private const string RealizedDocumentPathVariable = "COHESION_APPLICATION_MODEL_REALIZED_DOCUMENT_PATH";
    private const string ArgumentsPathVariable = "COHESION_APPLICATION_MODEL_ARGUMENTS_PATH";
    private const string DescendantPidPathVariable = "COHESION_APPLICATION_MODEL_DESCENDANT_PID_PATH";

    public static int Main(string[] args)
    {
        string? descendantPidPath = Environment.GetEnvironmentVariable(DescendantPidPathVariable);
        if (Array.Exists(args, static argument => argument == "--describe-descendant"))
        {
            if (!string.IsNullOrWhiteSpace(descendantPidPath))
            {
                File.WriteAllText(
                    descendantPidPath,
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            }

            Thread.Sleep(TimeSpan.FromSeconds(30));
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(descendantPidPath))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The test host process path is unavailable."),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--describe-descendant");
            using Process descendant = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The describe descendant could not be started.");
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            return 0;
        }

        string? documentPath = Environment.GetEnvironmentVariable(DescribeDocumentPathVariable);
        if (!string.IsNullOrWhiteSpace(documentPath))
        {
            string? argumentsPath = Environment.GetEnvironmentVariable(ArgumentsPathVariable);
            if (!string.IsNullOrWhiteSpace(argumentsPath))
            {
                File.AppendAllText(argumentsPath, string.Join('\u001f', args) + Environment.NewLine);
            }

            string? realizedPath = Environment.GetEnvironmentVariable(RealizedDocumentPathVariable);
            bool realize = Array.Exists(
                args,
                static argument => string.Equals(argument, "--realize", StringComparison.OrdinalIgnoreCase));
            Console.Out.Write(File.ReadAllText(
                realize && !string.IsNullOrWhiteSpace(realizedPath)
                    ? realizedPath
                    : documentPath));
            return 0;
        }

        string? pidPath = Environment.GetEnvironmentVariable(DescribePidPathVariable);
        Console.Out.Write(new string('o', 128 * 1024));
        Console.Error.Write(new string('e', 128 * 1024));
        Console.Out.Flush();
        Console.Error.Flush();

        if (!string.IsNullOrWhiteSpace(pidPath))
        {
            File.WriteAllText(
                pidPath,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
