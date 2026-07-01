using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Owns the lifetimes of the child processes the <see cref="LocalGateway"/> spawns. It is the
/// gateway's single writer of observed status into the state manager: it marks a process
/// <see cref="ResourceLifecycle.Starting"/> on launch, <see cref="ResourceLifecycle.Running"/>
/// on readiness, and <see cref="ResourceLifecycle.Failed"/> or <see cref="ResourceLifecycle.Stopped"/>
/// on exit, while piping the child's output with a <c>[resource-name]</c> prefix.
/// </summary>
internal sealed class LocalGatewayProcessSupervisor
{
    private readonly IApplicationResourceStateManager _state;
    private readonly LocalGatewayOptions _options;
    private readonly ConcurrentDictionary<ResourceId, RunningProcess> _processes = new();

    public LocalGatewayProcessSupervisor(IApplicationResourceStateManager state, LocalGatewayOptions options)
    {
        _state = state;
        _options = options;
    }

    public void Start(IApplicationResource resource, IExecutableArtifact artifact, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = artifact.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(artifact.ExecutablePath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (KeyValuePair<string, string> variable in environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var running = new RunningProcess(resource, process);

        process.OutputDataReceived += (_, args) => OnOutput(running, args.Data);
        process.ErrorDataReceived += (_, args) => OnOutput(running, args.Data);
        process.Exited += (_, _) => OnExited(running);

        _processes[resource.Id] = running;
        _state.SetState(resource.Id, ResourceLifecycle.Starting);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (_options.ReadyMarker is null)
        {
            _ = SettleAsync(running);
        }
    }

    public async Task StopAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        if (!_processes.TryRemove(resource.Id, out RunningProcess? running))
        {
            return;
        }

        running.Stopping = true;
        Process process = running.Process;

        try
        {
            if (!process.HasExited)
            {
                // MVP: forceful stop of the whole tree. A graceful, platform-specific SIGTERM /
                // console-Ctrl-C signal ahead of the kill is a planned follow-up.
                process.Kill(entireProcessTree: true);

                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(_options.StopGrace);

                try
                {
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The kill did not complete within the grace window; report Stopped regardless.
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process had already exited between the check and the kill.
        }
        finally
        {
            _state.SetState(resource.Id, ResourceLifecycle.Stopped);
            process.Dispose();
        }
    }

    private async Task SettleAsync(RunningProcess running)
    {
        try
        {
            await Task.Delay(_options.ReadySettle).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!running.Stopping && !running.Ready && !running.Process.HasExited)
        {
            running.Ready = true;
            _state.SetState(running.Resource.Id, ResourceLifecycle.Running);
        }
    }

    private void OnOutput(RunningProcess running, string? data)
    {
        if (data is null)
        {
            return;
        }

        Console.Out.WriteLine($"[{running.Resource.Name}] {data}");

        if (!running.Ready
            && _options.ReadyMarker is not null
            && data.Contains(_options.ReadyMarker, StringComparison.Ordinal))
        {
            running.Ready = true;
            _state.SetState(running.Resource.Id, ResourceLifecycle.Running);
        }
    }

    private void OnExited(RunningProcess running)
    {
        if (running.Stopping)
        {
            return; // StopAsync reports Stopped.
        }

        int exitCode = running.Process.ExitCode;
        _state.SetState(
            running.Resource.Id,
            exitCode == 0 ? ResourceLifecycle.Stopped : ResourceLifecycle.Failed,
            $"Process exited with code {exitCode}.");
    }

    private sealed class RunningProcess
    {
        public RunningProcess(IApplicationResource resource, Process process)
        {
            Resource = resource;
            Process = process;
        }

        public IApplicationResource Resource { get; }

        public Process Process { get; }

        public volatile bool Ready;

        public volatile bool Stopping;
    }
}
