using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalGatewayProcessSupervisor
{
    private const int ConfigurationExitCode = 64;
    private const int StartupExitCode = 70;

    private readonly IApplicationResourceStateManager _state;
    private readonly LocalGatewayOptions _options;
    private readonly LocalProbeRunner _probes;
    private readonly ConcurrentDictionary<ResourceId, SupervisedResource> _resources = new();

    public LocalGatewayProcessSupervisor(IApplicationResourceStateManager state, LocalGatewayOptions options)
    {
        _state = state;
        _options = options;
        _probes = new LocalProbeRunner(options);
    }

    public void Start(LocalResourceConfiguration configuration)
    {
        var supervised = new SupervisedResource(configuration);
        if (!_resources.TryAdd(configuration.Resource.Id, supervised))
        {
            throw new InvalidOperationException(
                $"Resource '{configuration.Resource.Name}' is already supervised by the local gateway.");
        }

        supervised.Completion = SuperviseAsync(supervised);
    }

    public async Task StopAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        if (!_resources.TryRemove(resource.Id, out SupervisedResource? supervised))
        {
            return;
        }

        supervised.StopRequested = true;
        supervised.Lifetime.Cancel();

        ProcessAttempt? attempt = supervised.GetAttempt();
        if (attempt is not null)
        {
            await StopProcessAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await supervised.Completion
                .WaitAsync(_options.StopGrace, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The existing force-kill path is the item-19 boundary. Item 20 owns stronger
            // process-group and graceful-stop guarantees.
        }
        finally
        {
            _state.SetState(resource.Id, ResourceLifecycle.Stopped);
            supervised.Lifetime.Dispose();
        }
    }

    private async Task SuperviseAsync(SupervisedResource supervised)
    {
        LocalResourceConfiguration configuration = supervised.Configuration;
        int restartAttempts = 0;

        try
        {
            while (!supervised.Lifetime.IsCancellationRequested)
            {
                _state.SetState(configuration.Resource.Id, ResourceLifecycle.Starting);
                ProcessAttempt attempt = StartProcess(supervised);
                AttemptOutcome startup = await AwaitReadinessAsync(
                    supervised,
                    attempt,
                    supervised.Lifetime.Token).ConfigureAwait(false);

                if (startup.Kind == AttemptOutcomeKind.FailFast)
                {
                    _state.SetState(
                        configuration.Resource.Id,
                        ResourceLifecycle.Failed,
                        startup.Detail);
                    await StopProcessAsync(attempt, CancellationToken.None).ConfigureAwait(false);
                    await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
                    return;
                }

                if (startup.Kind == AttemptOutcomeKind.Exited)
                {
                    await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
                    if (!ShouldRestart(configuration.RestartPolicy, startup.ExitCode))
                    {
                        SetExitState(configuration.Resource.Id, startup.ExitCode, startup.Detail);
                        return;
                    }

                    if (!await PrepareRestartAsync(
                            supervised,
                            attempt: null,
                            ++restartAttempts,
                            startup.Detail).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                _state.SetState(
                    configuration.Resource.Id,
                    ResourceLifecycle.Running,
                    observedEndpoints: configuration.ObservedEndpoints);

                AttemptOutcome steadyState = await MonitorRunningAsync(
                    supervised,
                    attempt,
                    supervised.Lifetime.Token).ConfigureAwait(false);

                if (steadyState.Kind == AttemptOutcomeKind.Exited)
                {
                    await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
                    if (!ShouldRestart(configuration.RestartPolicy, steadyState.ExitCode))
                    {
                        SetExitState(configuration.Resource.Id, steadyState.ExitCode, steadyState.Detail);
                        return;
                    }

                    if (steadyState.ExitCode != 0)
                    {
                        _state.SetState(
                            configuration.Resource.Id,
                            ResourceLifecycle.Degraded,
                            steadyState.Detail);
                    }

                    if (!await PrepareRestartAsync(
                            supervised,
                            attempt: null,
                            ++restartAttempts,
                            steadyState.Detail).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                if (!await PrepareRestartAsync(
                        supervised,
                        attempt,
                        ++restartAttempts,
                        steadyState.Detail).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (supervised.StopRequested)
        {
            ProcessAttempt? attempt = supervised.GetAttempt();
            if (attempt is not null)
            {
                await StopProcessAsync(attempt, CancellationToken.None).ConfigureAwait(false);
                await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
            }

            // StopAsync owns the final Stopped transition.
        }
        catch (Exception exception)
        {
            if (!supervised.StopRequested)
            {
                ProcessAttempt? attempt = supervised.GetAttempt();
                if (attempt is not null)
                {
                    await StopProcessAsync(attempt, CancellationToken.None).ConfigureAwait(false);
                    await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
                }

                _state.SetState(
                    configuration.Resource.Id,
                    ResourceLifecycle.Failed,
                    $"Local process supervision failed: {exception.Message}");
            }
        }
    }

    private ProcessAttempt StartProcess(SupervisedResource supervised)
    {
        LocalResourceConfiguration configuration = supervised.Configuration;
        var startInfo = new ProcessStartInfo
        {
            FileName = configuration.Artifact.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(configuration.Artifact.ExecutablePath)
                ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (KeyValuePair<string, string> variable in configuration.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        var process = new Process { StartInfo = startInfo };
        var attempt = new ProcessAttempt(process);
        supervised.SetAttempt(attempt);
        bool started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Executable '{configuration.Artifact.ExecutablePath}' did not start.");
            }

            started = true;
            attempt.Exit = process.WaitForExitAsync();
            attempt.StandardOutput = PumpAsync(
                process.StandardOutput,
                configuration.Resource.Name,
                isStandardOutput: true,
                configuration.ProbeStartMarker,
                attempt.MarkerSeen);
            attempt.StandardError = PumpAsync(
                process.StandardError,
                configuration.Resource.Name,
                isStandardOutput: false,
                marker: null,
                attempt.MarkerSeen);

            return attempt;
        }
        catch
        {
            if (started)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited while launch bookkeeping failed.
                }
            }

            supervised.ClearAttempt(attempt);
            process.Dispose();
            throw;
        }
    }

    private async Task<AttemptOutcome> AwaitReadinessAsync(
        SupervisedResource supervised,
        ProcessAttempt attempt,
        CancellationToken cancellationToken)
    {
        LocalResourceConfiguration configuration = supervised.Configuration;

        if (configuration.ProbeStartMarker is not null)
        {
            Task completed = await Task.WhenAny(
                attempt.MarkerSeen.Task,
                attempt.Exit,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);

            if (completed == attempt.Exit)
            {
                return ExitOutcome(attempt.Process, "Process exited before its ready line was observed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        AttemptOutcome startup = await RunProbeUntilSuccessAsync(
            configuration.StartupProbe,
            configuration,
            attempt,
            "startup",
            cancellationToken).ConfigureAwait(false);
        if (startup.Kind != AttemptOutcomeKind.Ready)
        {
            return startup;
        }

        AttemptOutcome readiness = await RunProbeUntilSuccessAsync(
            configuration.ReadinessProbe,
            configuration,
            attempt,
            "readiness",
            cancellationToken).ConfigureAwait(false);
        if (readiness.Kind != AttemptOutcomeKind.Ready)
        {
            return readiness;
        }

        if (configuration.MarkerIsReadiness && !attempt.MarkerSeen.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                $"Resource '{configuration.Resource.Name}' did not emit its configured ready marker.");
        }

        return AttemptOutcome.Ready();
    }

    private async Task<AttemptOutcome> RunProbeUntilSuccessAsync(
        IProbeSpec? probe,
        LocalResourceConfiguration configuration,
        ProcessAttempt attempt,
        string role,
        CancellationToken cancellationToken)
    {
        if (probe is null)
        {
            return AttemptOutcome.Ready();
        }

        while (true)
        {
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<ProbeAttemptResult> probeTask = _probes.RunAsync(
                probe,
                configuration,
                probeCancellation.Token);
            Task completed = await Task.WhenAny(probeTask, attempt.Exit).ConfigureAwait(false);

            if (completed == attempt.Exit)
            {
                probeCancellation.Cancel();
                await ObserveCancellationAsync(probeTask, probeCancellation.Token).ConfigureAwait(false);
                return ExitOutcome(attempt.Process, $"Process exited during its {role} probe.");
            }

            ProbeAttemptResult result = await probeTask.ConfigureAwait(false);
            if (result.Succeeded)
            {
                return AttemptOutcome.Ready();
            }

            if (result.FailFast)
            {
                return AttemptOutcome.FailFast(result.Detail);
            }

            completed = await Task.WhenAny(
                Task.Delay(_options.ProbeInterval, _options.TimeProvider, cancellationToken),
                attempt.Exit).ConfigureAwait(false);
            if (completed == attempt.Exit)
            {
                return ExitOutcome(attempt.Process, $"Process exited during its {role} probe.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<AttemptOutcome> MonitorRunningAsync(
        SupervisedResource supervised,
        ProcessAttempt attempt,
        CancellationToken cancellationToken)
    {
        LocalResourceConfiguration configuration = supervised.Configuration;
        IProbeSpec? liveness = configuration.LivenessProbe;
        if (liveness is null)
        {
            await attempt.Exit.WaitAsync(cancellationToken).ConfigureAwait(false);
            return ExitOutcome(attempt.Process, "Process exited while running.");
        }

        int consecutiveFailures = 0;
        while (true)
        {
            Task delay = Task.Delay(_options.ProbeInterval, _options.TimeProvider, cancellationToken);
            Task completed = await Task.WhenAny(delay, attempt.Exit).ConfigureAwait(false);
            if (completed == attempt.Exit)
            {
                return ExitOutcome(attempt.Process, "Process exited while running.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<ProbeAttemptResult> probeTask = _probes.RunAsync(
                liveness,
                configuration,
                probeCancellation.Token);
            completed = await Task.WhenAny(probeTask, attempt.Exit).ConfigureAwait(false);
            if (completed == attempt.Exit)
            {
                probeCancellation.Cancel();
                await ObserveCancellationAsync(probeTask, probeCancellation.Token).ConfigureAwait(false);
                return ExitOutcome(attempt.Process, "Process exited during its liveness probe.");
            }

            ProbeAttemptResult result = await probeTask.ConfigureAwait(false);
            if (result.Succeeded)
            {
                consecutiveFailures = 0;
                if (_state.GetState(configuration.Resource.Id) == ResourceLifecycle.Degraded)
                {
                    _state.SetState(
                        configuration.Resource.Id,
                        ResourceLifecycle.Running,
                        observedEndpoints: configuration.ObservedEndpoints);
                }

                continue;
            }

            consecutiveFailures++;
            _state.SetState(
                configuration.Resource.Id,
                ResourceLifecycle.Degraded,
                result.Detail,
                configuration.ObservedEndpoints);

            if (consecutiveFailures < _options.LivenessFailureThreshold
                || configuration.RestartPolicy == RestartPolicy.Never)
            {
                continue;
            }

            return AttemptOutcome.Restart(result.Detail);
        }
    }

    private async Task<bool> PrepareRestartAsync(
        SupervisedResource supervised,
        ProcessAttempt? attempt,
        int restartAttempt,
        string detail)
    {
        LocalResourceConfiguration configuration = supervised.Configuration;
        _state.SetState(configuration.Resource.Id, ResourceLifecycle.Stopping, detail);

        if (attempt is not null)
        {
            await StopProcessAsync(attempt, CancellationToken.None).ConfigureAwait(false);
            await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
        }

        if (restartAttempt > _options.MaximumRestartAttempts)
        {
            _state.SetState(
                configuration.Resource.Id,
                ResourceLifecycle.Failed,
                $"Restart limit {_options.MaximumRestartAttempts} was exhausted. Last failure: {detail}");
            return false;
        }

        TimeSpan backoff = CalculateBackoff(restartAttempt);
        await Task.Delay(backoff, _options.TimeProvider, supervised.Lifetime.Token).ConfigureAwait(false);
        return true;
    }

    private TimeSpan CalculateBackoff(int restartAttempt)
    {
        double multiplier = Math.Pow(2, restartAttempt - 1);
        double ticks = Math.Min(
            _options.InitialRestartBackoff.Ticks * multiplier,
            _options.MaximumRestartBackoff.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private async Task StopProcessAsync(ProcessAttempt attempt, CancellationToken cancellationToken)
    {
        Process process = attempt.Process;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);

                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(_options.StopGrace);
                try
                {
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Item 20 owns stronger shutdown and process-group behavior.
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static async Task DrainAndDisposeAsync(
        SupervisedResource supervised,
        ProcessAttempt attempt)
    {
        try
        {
            await attempt.Exit.ConfigureAwait(false);
            await Task.WhenAll(attempt.StandardOutput, attempt.StandardError).ConfigureAwait(false);
        }
        finally
        {
            supervised.ClearAttempt(attempt);
            attempt.Process.Dispose();
        }
    }

    private static async Task ObserveCancellationAsync(
        Task probeTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await probeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        ResourceName resource,
        bool isStandardOutput,
        string? marker,
        TaskCompletionSource markerSeen)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            string prefixed = $"[{resource}] {line}";
            if (isStandardOutput)
            {
                Console.Out.WriteLine(prefixed);
            }
            else
            {
                Console.Error.WriteLine(prefixed);
            }

            if (marker is not null && line.Contains(marker, StringComparison.Ordinal))
            {
                markerSeen.TrySetResult();
            }
        }
    }

    internal static bool ShouldRestart(RestartPolicy policy, int exitCode)
    {
        if (policy == RestartPolicy.Never
            || exitCode is ConfigurationExitCode or StartupExitCode)
        {
            return false;
        }

        return policy == RestartPolicy.Always
            || (policy == RestartPolicy.OnFailure && exitCode != 0);
    }

    private void SetExitState(ResourceId resource, int exitCode, string detail)
    {
        _state.SetState(
            resource,
            exitCode == 0 ? ResourceLifecycle.Stopped : ResourceLifecycle.Failed,
            detail);
    }

    private static AttemptOutcome ExitOutcome(Process process, string prefix)
    {
        int exitCode = process.ExitCode;
        return AttemptOutcome.Exited(
            exitCode,
            $"{prefix} Exit code: {exitCode}.");
    }

    private enum AttemptOutcomeKind
    {
        Ready,
        Exited,
        FailFast,
        Restart,
    }

    private readonly record struct AttemptOutcome(
        AttemptOutcomeKind Kind,
        int ExitCode,
        string Detail)
    {
        public static AttemptOutcome Ready() => new(AttemptOutcomeKind.Ready, 0, string.Empty);

        public static AttemptOutcome Exited(int exitCode, string detail)
            => new(AttemptOutcomeKind.Exited, exitCode, detail);

        public static AttemptOutcome FailFast(string detail)
            => new(AttemptOutcomeKind.FailFast, 0, detail);

        public static AttemptOutcome Restart(string detail)
            => new(AttemptOutcomeKind.Restart, 0, detail);
    }

    private sealed class SupervisedResource
    {
        private readonly object _gate = new();
        private ProcessAttempt? _attempt;

        public SupervisedResource(LocalResourceConfiguration configuration)
        {
            Configuration = configuration;
        }

        public LocalResourceConfiguration Configuration { get; }

        public CancellationTokenSource Lifetime { get; } = new();

        public Task Completion { get; set; } = Task.CompletedTask;

        public volatile bool StopRequested;

        public ProcessAttempt? GetAttempt()
        {
            lock (_gate)
            {
                return _attempt;
            }
        }

        public void SetAttempt(ProcessAttempt attempt)
        {
            lock (_gate)
            {
                _attempt = attempt;
            }
        }

        public void ClearAttempt(ProcessAttempt attempt)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_attempt, attempt))
                {
                    _attempt = null;
                }
            }
        }
    }

    private sealed class ProcessAttempt
    {
        public ProcessAttempt(Process process)
        {
            Process = process;
        }

        public Process Process { get; }

        public Task Exit { get; set; } = Task.CompletedTask;

        public Task StandardOutput { get; set; } = Task.CompletedTask;

        public Task StandardError { get; set; } = Task.CompletedTask;

        public TaskCompletionSource MarkerSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
