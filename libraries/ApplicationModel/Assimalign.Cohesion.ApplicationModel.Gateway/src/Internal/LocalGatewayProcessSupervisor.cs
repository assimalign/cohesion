using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalGatewayProcessSupervisor
{
    private const int ConfigurationExitCode = 64;
    private const int StartupExitCode = 70;

    private readonly IApplicationResourceStateManager _state;
    private readonly LocalGatewayOptions _options;
    private readonly LocalProcessStateStore _processState;
    private readonly LocalProbeRunner _probes;
    private readonly ConcurrentDictionary<ResourceId, SupervisedResource> _resources = new();
    private ApplicationName _application;
    private bool _restartOrphans;
    private bool _initialized;

    public LocalGatewayProcessSupervisor(
        IApplicationResourceStateManager state,
        LocalGatewayOptions options,
        LocalProcessStateStore processState)
    {
        _state = state;
        _options = options;
        _processState = processState;
        _probes = new LocalProbeRunner(options);
    }

    public async Task InitializeAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        await _processState.InitializeAsync(model, cancellationToken).ConfigureAwait(false);
        _application = model.Name;
        _restartOrphans = _options.RestartOrphans || model.RestartOrphans;
        _initialized = true;
    }

    public async Task StartAsync(
        LocalResourceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The local process supervisor has not been initialized for an application.");
        }

        if (_resources.ContainsKey(configuration.Resource.Id))
        {
            // Reconcile is level-triggered. Mount files have already been refreshed by the
            // local preparer; an already supervised process does not need a duplicate loop.
            return;
        }

        var supervised = new SupervisedResource(configuration);
        if (!_resources.TryAdd(configuration.Resource.Id, supervised))
        {
            throw new InvalidOperationException(
                $"Resource '{configuration.Resource.Name}' is already supervised by the local gateway.");
        }

        try
        {
            ProcessAttempt? recovered = await TryRecoverAsync(configuration, cancellationToken).ConfigureAwait(false);
            if (recovered is not null && (_restartOrphans || recovered.RequiresRelaunch))
            {
                _state.SetState(
                    configuration.Resource.Id,
                    ResourceLifecycle.Stopping,
                    recovered.RequiresRelaunch
                        ? "Replacing a verified child whose executable no longer matches desired state."
                        : "Replacing a verified child process left by an earlier gateway instance.");
                ProcessStopResult result = await StopAttemptAsync(
                    supervised,
                    recovered,
                    cancellationToken).ConfigureAwait(false);
                await DrainAndDisposeAsync(supervised, recovered).ConfigureAwait(false);
                if (result == ProcessStopResult.Forced)
                {
                    _state.SetState(
                        configuration.Resource.Id,
                        ResourceLifecycle.Stopping,
                        "The orphan ignored its graceful-stop signal and was force-killed before relaunch.");
                }

                recovered = null;
            }

            supervised.Completion = SuperviseAsync(supervised, recovered);
        }
        catch
        {
            _resources.TryRemove(configuration.Resource.Id, out _);
            supervised.Lifetime.Dispose();
            supervised.ForceStop.Dispose();
            throw;
        }
    }

    public async Task StopAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        if (!_resources.TryRemove(resource.Id, out SupervisedResource? supervised))
        {
            return;
        }

        supervised.StopRequested = true;
        _state.SetState(resource.Id, ResourceLifecycle.Stopping);
        supervised.Lifetime.Cancel();

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            supervised.ForceStop);

        try
        {
            await supervised.Completion.ConfigureAwait(false);
        }
        finally
        {
            if (supervised.StopResult == ProcessStopResult.Forced)
            {
                _state.SetState(
                    resource.Id,
                    ResourceLifecycle.Failed,
                    $"Failed(forced): resource ignored its graceful-stop signal for "
                    + $"{supervised.Configuration.StopGrace.TotalSeconds:0.###} seconds and was killed.");
            }
            else
            {
                _state.SetState(resource.Id, ResourceLifecycle.Stopped);
            }

            supervised.Lifetime.Dispose();
            supervised.ForceStop.Dispose();
        }
    }

    public async Task UninstallAsync(
        IApplicationResource resource,
        TimeSpan stopGrace,
        CancellationToken cancellationToken)
    {
        if (_resources.ContainsKey(resource.Id))
        {
            await StopAsync(resource, cancellationToken).ConfigureAwait(false);
            return;
        }

        LocalProcessRegistration? registration = await _processState
            .LoadAsync(_application, resource.Name, cancellationToken)
            .ConfigureAwait(false);
        if (registration is null)
        {
            return;
        }

        Process? process = null;
        bool verified = false;
        try
        {
            process = Process.GetProcessById(registration.ProcessId);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != registration.StartTimeUtcTicks)
            {
                _processState.DeleteStale(_application, resource.Name);
                return;
            }

            string? executablePath = await ObserveExecutablePathAsync(
                process,
                cancellationToken).ConfigureAwait(false);
            if (executablePath is null
                || !PathEquals(executablePath, registration.ExecutablePath))
            {
                _processState.DeleteStale(_application, resource.Name);
                return;
            }

            verified = true;
            var attempt = new ProcessAttempt(process, registration)
            {
                Exit = process.WaitForExitAsync(),
            };
            ProcessStopResult result = await StopProcessCoreAsync(
                attempt,
                stopGrace,
                cancellationToken).ConfigureAwait(false);
            await _processState.DeleteIfMatchesAsync(
                _application,
                resource.Name,
                registration).ConfigureAwait(false);
            _state.SetState(
                resource.Id,
                result == ProcessStopResult.Clean
                    ? ResourceLifecycle.Stopped
                    : ResourceLifecycle.Failed,
                result == ProcessStopResult.Forced
                    ? "Failed(forced): observed process ignored its graceful-stop signal during uninstall."
                    : null);
        }
        catch (ArgumentException) when (!verified)
        {
            _processState.DeleteStale(_application, resource.Name);
        }
        catch (InvalidOperationException) when (!verified)
        {
            _processState.DeleteStale(_application, resource.Name);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task SuperviseAsync(
        SupervisedResource supervised,
        ProcessAttempt? recovered)
    {
        LocalResourceConfiguration configuration = supervised.Configuration;
        int restartAttempts = 0;
        ProcessAttempt? initialAttempt = recovered;

        try
        {
            while (!supervised.Lifetime.IsCancellationRequested)
            {
                _state.SetState(configuration.Resource.Id, ResourceLifecycle.Starting);
                ProcessAttempt attempt = initialAttempt
                    ?? await StartProcessAsync(supervised).ConfigureAwait(false);
                initialAttempt = null;
                supervised.SetAttempt(attempt);

                AttemptOutcome startup = await AwaitReadinessAsync(
                    supervised,
                    attempt,
                    supervised.Lifetime.Token).ConfigureAwait(false);

                if (startup.Kind == AttemptOutcomeKind.FailFast)
                {
                    ProcessStopResult stopResult = await StopAttemptAsync(
                        supervised,
                        attempt,
                        CancellationToken.None).ConfigureAwait(false);
                    await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
                    string detail = stopResult == ProcessStopResult.Forced
                        ? $"{startup.Detail} Failed(forced) while stopping the rejected attempt."
                        : startup.Detail;
                    _state.SetState(configuration.Resource.Id, ResourceLifecycle.Failed, detail);
                    return;
                }

                if (startup.Kind == AttemptOutcomeKind.Exited)
                {
                    await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
                    if (supervised.StopRequested)
                    {
                        return;
                    }

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
                    if (supervised.StopRequested)
                    {
                        return;
                    }

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
        }
        catch (Exception exception)
        {
            ProcessAttempt? attempt = supervised.GetAttempt();
            if (attempt is not null)
            {
                ProcessStopResult result = await StopAttemptAsync(
                    supervised,
                    attempt,
                    CancellationToken.None).ConfigureAwait(false);
                if (supervised.StopRequested)
                {
                    supervised.StopResult = result;
                }

                await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
            }

            if (!supervised.StopRequested)
            {
                _state.SetState(
                    configuration.Resource.Id,
                    ResourceLifecycle.Failed,
                    $"Local process supervision failed: {exception.Message}");
            }
        }
        finally
        {
            if (supervised.StopRequested)
            {
                ProcessAttempt? attempt = supervised.GetAttempt();
                if (attempt is not null)
                {
                    supervised.StopResult = await StopAttemptAsync(
                        supervised,
                        attempt,
                        CancellationToken.None).ConfigureAwait(false);
                    await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<ProcessAttempt?> TryRecoverAsync(
        LocalResourceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        LocalProcessRegistration? registration = await _processState.LoadAsync(
            _application,
            configuration.Resource.Name,
            cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(registration.ProcessId);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != registration.StartTimeUtcTicks)
            {
                process.Dispose();
                _processState.DeleteStale(_application, configuration.Resource.Name);
                return null;
            }

            string? executablePath = await ObserveExecutablePathAsync(
                process,
                cancellationToken).ConfigureAwait(false);
            if (executablePath is null
                || !PathEquals(executablePath, registration.ExecutablePath))
            {
                process.Dispose();
                _processState.DeleteStale(_application, configuration.Resource.Name);
                return null;
            }

            var attempt = new ProcessAttempt(
                process,
                registration,
                requiresRelaunch: !PathEquals(
                    registration.ExecutablePath,
                    configuration.Artifact.ExecutablePath));
            attempt.Exit = process.WaitForExitAsync();
            attempt.MarkerSeen.TrySetResult();
            return attempt;
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            _processState.DeleteStale(_application, configuration.Resource.Name);
            return null;
        }
        catch (InvalidOperationException)
        {
            process?.Dispose();
            _processState.DeleteStale(_application, configuration.Resource.Name);
            return null;
        }
    }

    private async Task<ProcessAttempt> StartProcessAsync(SupervisedResource supervised)
    {
        LocalResourceConfiguration configuration = supervised.Configuration;
        string executablePath = Path.GetFullPath(configuration.Artifact.ExecutablePath);
        string? setSessionPath = FindSetSessionExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = setSessionPath ?? executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
                ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        if (setSessionPath is not null)
        {
            startInfo.ArgumentList.Add(executablePath);
        }

        if (OperatingSystem.IsWindows())
        {
            startInfo.CreateNewProcessGroup = true;
        }

        foreach (KeyValuePair<string, string> variable in configuration.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        EventWaitHandle? stopEvent = null;
        string? stopEventName = null;
        if (OperatingSystem.IsWindows() && configuration.UseStopEvent)
        {
            stopEventName = $"Global\\cohesion-{configuration.Resource.Name}-{Guid.NewGuid():N}-stop";
            stopEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                stopEventName,
                out _);
            startInfo.Environment[ResourceEnvironment.StopEvent] = stopEventName;
        }

        var process = new Process { StartInfo = startInfo };
        bool started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Executable '{configuration.Artifact.ExecutablePath}' did not start.");
            }

            started = true;
            bool hasProcessGroup = OperatingSystem.IsWindows()
                || setSessionPath is not null
                || LocalProcessSignal.TryCreateProcessGroup(process.Id);
            var registration = new LocalProcessRegistration
            {
                ProcessId = process.Id,
                StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ExecutablePath = CanonicalizePath(executablePath),
                HasProcessGroup = hasProcessGroup,
                StopEventName = stopEventName,
            };
            await _processState.SaveAsync(
                _application,
                configuration.Resource.Name,
                registration,
                CancellationToken.None).ConfigureAwait(false);

            var attempt = new ProcessAttempt(process, registration, stopEvent);
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
            stopEvent?.Dispose();
            if (started)
            {
                TryForceKill(process, hasProcessGroup: OperatingSystem.IsWindows() || setSessionPath is not null);
                try
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }

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
            ProcessStopResult result = await StopAttemptAsync(
                supervised,
                attempt,
                CancellationToken.None).ConfigureAwait(false);
            await DrainAndDisposeAsync(supervised, attempt).ConfigureAwait(false);
            if (result == ProcessStopResult.Forced)
            {
                detail += " The unhealthy attempt ignored graceful stop and was force-killed.";
            }
        }

        if (restartAttempt > _options.MaximumRestartAttempts)
        {
            _state.SetState(
                configuration.Resource.Id,
                ResourceLifecycle.Failed,
                $"Failed(restartable): restart limit {_options.MaximumRestartAttempts} was exhausted. "
                + $"Last failure: {detail}");
            return false;
        }

        TimeSpan backoff = CalculateRestartBackoff(
            restartAttempt,
            _options.InitialRestartBackoff,
            _options.MaximumRestartBackoff);
        await Task.Delay(backoff, _options.TimeProvider, supervised.Lifetime.Token).ConfigureAwait(false);
        return true;
    }

    internal static TimeSpan CalculateRestartBackoff(
        int restartAttempt,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(restartAttempt, 1);
        long ticks = initialBackoff.Ticks;
        for (int attempt = 1; attempt < restartAttempt && ticks < maximumBackoff.Ticks; attempt++)
        {
            ticks = ticks > maximumBackoff.Ticks / 2
                ? maximumBackoff.Ticks
                : ticks * 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, maximumBackoff.Ticks));
    }

    private async Task<ProcessStopResult> StopAttemptAsync(
        SupervisedResource supervised,
        ProcessAttempt attempt,
        CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            supervised.ForceStop);

        return await attempt.GetOrStartStop(
            () => StopProcessCoreAsync(
                attempt,
                supervised.Configuration.StopGrace,
                supervised.ForceStop.Token)).ConfigureAwait(false);
    }

    private static async Task<ProcessStopResult> StopProcessCoreAsync(
        ProcessAttempt attempt,
        TimeSpan stopGrace,
        CancellationToken forceStop)
    {
        Process process = attempt.Process;
        if (HasExited(process))
        {
            return ProcessStopResult.Clean;
        }

        TrySignalGracefulStop(attempt);

        Task grace = Task.Delay(stopGrace);
        Task force = Task.Delay(Timeout.InfiniteTimeSpan, forceStop);
        Task completed = await Task.WhenAny(attempt.Exit, grace, force).ConfigureAwait(false);
        if (completed == attempt.Exit || HasExited(process))
        {
            await attempt.Exit.ConfigureAwait(false);
            return ProcessStopResult.Clean;
        }

        TryForceKill(process, attempt.Registration.HasProcessGroup);
        await attempt.Exit.ConfigureAwait(false);
        return ProcessStopResult.Forced;
    }

    private static void TrySignalGracefulStop(ProcessAttempt attempt)
    {
        if (OperatingSystem.IsWindows())
        {
            bool eventSignaled = false;
            try
            {
                if (attempt.StopEvent is not null)
                {
                    eventSignaled = attempt.StopEvent.Set();
                }
                else if (!string.IsNullOrWhiteSpace(attempt.Registration.StopEventName))
                {
                    using EventWaitHandle stopEvent = EventWaitHandle.OpenExisting(
                        attempt.Registration.StopEventName);
                    eventSignaled = stopEvent.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (!eventSignaled && attempt.Registration.HasProcessGroup)
            {
                LocalProcessSignal.TrySendControlBreak(attempt.Process.Id);
            }

            return;
        }

        LocalProcessSignal.TrySendTerminate(
            attempt.Process.Id,
            attempt.Registration.HasProcessGroup);
    }

    private static void TryForceKill(Process process, bool hasProcessGroup)
    {
        try
        {
            if (HasExited(process))
            {
                return;
            }

            if (hasProcessGroup && LocalProcessSignal.TryForceKillGroup(process.Id))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private async Task DrainAndDisposeAsync(
        SupervisedResource supervised,
        ProcessAttempt attempt)
    {
        try
        {
            await attempt.Exit.ConfigureAwait(false);
            await Task.WhenAll(attempt.StandardOutput, attempt.StandardError).ConfigureAwait(false);
            await _processState.DeleteIfMatchesAsync(
                _application,
                supervised.Configuration.Resource.Name,
                attempt.Registration).ConfigureAwait(false);
        }
        finally
        {
            supervised.ClearAttempt(attempt);
            attempt.StopEvent?.Dispose();
            attempt.Process.Dispose();
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string GetExecutablePath(Process process)
    {
        string? path = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Could not observe the executable path for process {process.Id}.");
        }

        return CanonicalizePath(path);
    }

    private static async Task<string?> ObserveExecutablePathAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (HasExited(process))
            {
                return null;
            }

            try
            {
                return GetExecutablePath(process);
            }
            catch (InvalidOperationException) when (!HasExited(process))
            {
            }
            catch (System.ComponentModel.Win32Exception) when (!HasExited(process))
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            process.Refresh();
        }

        throw new IOException(
            $"Could not verify the executable path for running process {process.Id}.");
    }

    private static string? FindSetSessionExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        const string usrBinSetSid = "/usr/bin/setsid";
        if (File.Exists(usrBinSetSid))
        {
            return usrBinSetSid;
        }

        const string binSetSid = "/bin/setsid";
        return File.Exists(binSetSid) ? binSetSid : null;
    }

    private static bool PathEquals(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(CanonicalizePath(left), CanonicalizePath(right), comparison);
    }

    private static string CanonicalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        FileSystemInfo? target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
        return target?.FullName ?? fullPath;
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
        if (exitCode == 0)
        {
            _state.SetState(resource, ResourceLifecycle.Stopped, detail);
            return;
        }

        string classification = exitCode is ConfigurationExitCode or StartupExitCode
            ? "Failed(final)"
            : "Failed(restartable)";
        _state.SetState(resource, ResourceLifecycle.Failed, $"{classification}: {detail}");
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

    private enum ProcessStopResult
    {
        Clean,
        Forced,
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

        public CancellationTokenSource ForceStop { get; } = new();

        public Task Completion { get; set; } = Task.CompletedTask;

        public volatile bool StopRequested;

        public ProcessStopResult StopResult { get; set; }

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
        private readonly object _stopGate = new();
        private Task<ProcessStopResult>? _stop;

        public ProcessAttempt(
            Process process,
            LocalProcessRegistration registration,
            EventWaitHandle? stopEvent = null,
            bool requiresRelaunch = false)
        {
            Process = process;
            Registration = registration;
            StopEvent = stopEvent;
            RequiresRelaunch = requiresRelaunch;
        }

        public Process Process { get; }

        public LocalProcessRegistration Registration { get; }

        public EventWaitHandle? StopEvent { get; }

        public bool RequiresRelaunch { get; }

        public Task Exit { get; set; } = Task.CompletedTask;

        public Task StandardOutput { get; set; } = Task.CompletedTask;

        public Task StandardError { get; set; } = Task.CompletedTask;

        public TaskCompletionSource MarkerSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessStopResult> GetOrStartStop(Func<Task<ProcessStopResult>> start)
        {
            lock (_stopGate)
            {
                return _stop ??= start();
            }
        }
    }
}
