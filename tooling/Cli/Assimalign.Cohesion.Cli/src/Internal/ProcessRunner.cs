using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Cli;

internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Inherit all console streams: in particular, never capture or log trust-issue stdout.
        using Process process = Process.Start(start) ?? throw new CliException("Unable to start dotnet.");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Console Ctrl+C also reaches dotnet. Allow the gateway's 30-second stop grace.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (grace.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        return process.ExitCode;
    }
}
