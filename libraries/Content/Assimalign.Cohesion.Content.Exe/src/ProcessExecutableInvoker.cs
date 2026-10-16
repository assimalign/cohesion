using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Exe.Internal;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>A stateless invoker owning each process and its redirected pipes until cleanup completes.</summary>
/// <remarks>Cancellation returns a result. Tree cleanup uses platform Process semantics; this is not
/// containment for programs that deliberately detach or reparent themselves.</remarks>
public sealed class ProcessExecutableInvoker : IExecutableInvoker
{
    /// <summary>Creates an invoker without allocating a process.</summary>
    public ProcessExecutableInvoker() { }

    /// <inheritdoc/>
    public async ValueTask<ExecutableResult> InvokeAsync(ExecutableStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        if (cancellationToken.IsCancellationRequested)
        {
            return EmptyResult(ExecutableTerminationReason.Canceled, startedAt, clock.Elapsed);
        }
        if (startInfo.Timeout == TimeSpan.Zero)
        {
            return EmptyResult(ExecutableTerminationReason.TimedOut, startedAt, clock.Elapsed);
        }
        var launch = new ProcessStartInfo
        {
            FileName = startInfo.ExecutablePath,
            WorkingDirectory = startInfo.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in startInfo.Arguments)
        {
            launch.ArgumentList.Add(argument);
        }
        foreach (var pair in startInfo.Environment)
        {
            if (pair.Value is null)
            {
                launch.Environment.Remove(pair.Key);
            }
            else
            {
                launch.Environment[pair.Key] = pair.Value;
            }
        }

        // Allocate before launch so allocation failure cannot strand a process.
        using var stdout = new BoundedCapture(startInfo.MaxStandardOutputBytes);
        using var stderr = new BoundedCapture(startInfo.MaxStandardErrorBytes);
        using var process = new Process { StartInfo = launch };
        using var timeout = new CancellationTokenSource();
        if (startInfo.Timeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }
        using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var pipeCancellation = new CancellationTokenSource();
        var ioTasks = new List<Task>(3);
        try
        {
            if (!process.Start())
            {
                return EmptyResult(ExecutableTerminationReason.FailedToStart, startedAt, clock.Elapsed);
            }
        }
        catch (Win32Exception)
        {
            return EmptyResult(ExecutableTerminationReason.FailedToStart, startedAt, clock.Elapsed);
        }
        catch (InvalidOperationException)
        {
            return EmptyResult(ExecutableTerminationReason.FailedToStart, startedAt, clock.Elapsed);
        }
        catch (IOException)
        {
            return EmptyResult(ExecutableTerminationReason.FailedToStart, startedAt, clock.Elapsed);
        }
        catch (UnauthorizedAccessException)
        {
            return EmptyResult(ExecutableTerminationReason.FailedToStart, startedAt, clock.Elapsed);
        }

        var completed = false;
        var reason = ExecutableTerminationReason.Exited;
        try
        {
            // Both drains begin before writing input or awaiting exit.
            ioTasks.Add(stdout.DrainAsync(process.StandardOutput.BaseStream, pipeCancellation.Token));
            ioTasks.Add(stderr.DrainAsync(process.StandardError.BaseStream, pipeCancellation.Token));
            ioTasks.Add(WriteInputAsync(process.StandardInput, startInfo.StandardInput, pipeCancellation.Token));
            var pending = new List<Task>(ioTasks) { process.WaitForExitAsync(CancellationToken.None) };
            while (pending.Count != 0)
            {
                // Observe pipe failures immediately, rather than waiting for every task to complete.
                var next = await Task.WhenAny(pending).WaitAsync(lifecycle.Token).ConfigureAwait(false);
                await next.ConfigureAwait(false);
                pending.Remove(next);
            }
            completed = true;
        }
        catch (OperationCanceledException) when (lifecycle.IsCancellationRequested)
        {
            reason = cancellationToken.IsCancellationRequested ? ExecutableTerminationReason.Canceled : ExecutableTerminationReason.TimedOut;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        catch (Win32Exception) when (process.HasExited) { }
                    }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    // A descendant may still hold a pipe while the OS finishes killing it.
                    // Stop pending reads after root exit, then observe them before returning buffers.
                    await pipeCancellation.CancelAsync().ConfigureAwait(false);
                    foreach (var task in ioTasks)
                    {
                        try { await task.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                    }
                }
            }
        }
        return ExecutableResult.Create(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated, stderr.Truncated,
            startedAt, startedAt + clock.Elapsed, reason);
    }

    private static ExecutableResult EmptyResult(ExecutableTerminationReason reason, DateTimeOffset startedAt, TimeSpan elapsed) =>
        ExecutableResult.Create(null, string.Empty, string.Empty, false, false, startedAt, startedAt + elapsed, reason);

    private static async Task WriteInputAsync(StreamWriter writer, string? content, CancellationToken cancellationToken = default)
    {
        try
        {
            if (content is not null)
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Programs may consume only a prefix of stdin or exit immediately.
        }
        finally
        {
            try { writer.Dispose(); }
            catch (IOException) { }
        }
    }
}
