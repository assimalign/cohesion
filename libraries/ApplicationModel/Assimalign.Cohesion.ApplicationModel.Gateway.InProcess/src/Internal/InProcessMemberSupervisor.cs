using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

internal interface IInProcessMemberSupervisor
{
    Task StartAsync(
        InProcessMemberConfiguration configuration,
        CancellationToken cancellationToken);

    Task StopAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken);
}

internal sealed class InProcessMemberSupervisor : IInProcessMemberSupervisor
{
    private const int successExitCode = 0;
    private const int configurationExitCode = 64;
    private const int dependencyExitCode = 69;
    private const int startupExitCode = 70;
    private const int runtimeExitCode = 75;
    private const int interruptedExitCode = 130;
    private const int terminatedExitCode = 143;

    private readonly Lock _lock = new();
    private readonly Dictionary<InProcessMemberKey, InProcessMember> _members = [];
    private readonly ProcessHost _host;
    private readonly InProcessGatewayOptions _options;
    private readonly IInProcessProbeRunner _probes;
    private readonly IResourceEntryInvoker _entries;

    internal InProcessMemberSupervisor(
        ProcessHost host,
        InProcessGatewayOptions options,
        IInProcessProbeRunner probes,
        IResourceEntryInvoker entries)
    {
        _host = host;
        _options = options;
        _probes = probes;
        _entries = entries;
    }

    public async Task StartAsync(
        InProcessMemberConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var key = new InProcessMemberKey(
            configuration.Control.Model.Name,
            configuration.Control.Resource.Id);
        InProcessMember? previous;
        lock (_lock)
        {
            _members.TryGetValue(key, out previous);
        }
        bool isReplacement = previous is not null;

        // ResourceContext is intentionally immutable and area hosts snapshot credentials and
        // other bridge values while building. A new reconcile pass therefore replaces the
        // generation so rotated inputs are actually delivered instead of being silently dropped.
        if (previous is not null)
        {
            await StopMemberAsync(previous, configuration.Control, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var member = new InProcessMember(key, configuration);
        lock (_lock)
        {
            if (_members.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"Resource '{configuration.Control.Resource.Name}' is already being reconciled in process.");
            }

            _members.Add(key, member);
        }

        bool disposeMember = false;
        await member.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await StartInitialGenerationAsync(member, cancellationToken).ConfigureAwait(false))
            {
                member.Monitor = MonitorAsync(member);
            }
            else
            {
                Remove(member);
                disposeMember = true;
            }
        }
        catch (Exception exception)
        {
            member.Lifetime.Cancel();
            Exception? cleanupFailure = await StopGenerationCapturingAsync(
                member,
                CancellationToken.None).ConfigureAwait(false);
            string detail = DescribeFailure(
                member,
                GetExitCode(exception, hasReachedReadiness: false),
                exception);
            if (cleanupFailure is not null)
            {
                detail += $" Host cleanup failed: {cleanupFailure.Message}";
            }
            configuration.Control.State.SetState(
                configuration.Control.Resource.Id,
                ResourceLifecycle.Failed,
                detail,
                configuration.ObservedEndpoints);
            Remove(member);
            disposeMember = true;
            throw;
        }
        finally
        {
            member.Gate.Release();
            if (disposeMember)
            {
                member.Dispose();
            }
        }

        if (isReplacement)
        {
            IReadOnlySet<ResourceLifecycle> terminals = new HashSet<ResourceLifecycle>
            {
                ResourceLifecycle.Running,
                ResourceLifecycle.Failed,
                ResourceLifecycle.Stopped,
            };
            ResourceLifecycle reached = await configuration.Control.State
                .WaitForStateAsync(
                    configuration.Control.Resource.Id,
                    terminals,
                    _options.ReadinessBudget,
                    cancellationToken)
                .ConfigureAwait(false);
            if (reached is not ResourceLifecycle.Running)
            {
                throw new InvalidOperationException(
                    $"Reconciled in-process resource '{configuration.Control.Resource.Name}' "
                    + $"did not reach Running within '{_options.ReadinessBudget}' "
                    + $"(observed '{reached}').");
            }
        }
    }

    private async Task<bool> StartInitialGenerationAsync(
        InProcessMember member,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await StartGenerationAsync(member, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                int exitCode = GetExitCode(exception, hasReachedReadiness: false);
                string detail = DescribeFailure(member, exitCode, exception);
                Exception? cleanupFailure = await StopGenerationCapturingAsync(
                    member,
                    CancellationToken.None).ConfigureAwait(false);
                if (cleanupFailure is not null)
                {
                    member.Configuration.Control.State.SetState(
                        member.Configuration.Control.Resource.Id,
                        ResourceLifecycle.Failed,
                        $"{detail} Host cleanup failed: {cleanupFailure.Message}",
                        member.Configuration.ObservedEndpoints);
                    return false;
                }

                if (!ShouldRestart(exitCode, member.Configuration.RestartPolicy))
                {
                    SetTerminalState(member, exitCode, detail);
                    return false;
                }

                member.Configuration.Control.State.SetState(
                    member.Configuration.Control.Resource.Id,
                    ResourceLifecycle.Degraded,
                    detail,
                    member.Configuration.ObservedEndpoints);
                if (!await WaitForRestartAttemptAsync(member, detail, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return false;
                }
            }
        }
    }

    public async Task StopAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        InProcessMember? member;
        lock (_lock)
        {
            _members.TryGetValue(
                new InProcessMemberKey(context.Model.Name, context.Resource.Id),
                out member);
        }
        if (member is null)
        {
            return;
        }

        await StopMemberAsync(member, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopMemberAsync(
        InProcessMember member,
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        member.Lifetime.Cancel();
        Task? monitor = member.Monitor;
        if (monitor is not null)
        {
            try
            {
                await monitor.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (member.Lifetime.IsCancellationRequested)
            {
                // The monitor observes the lifetime request and exits before teardown.
            }
        }

        bool removed = false;
        await member.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Stopping,
                observedEndpoints: member.Configuration.ObservedEndpoints);
            await StopGenerationAsync(member, cancellationToken).ConfigureAwait(false);
            context.State.SetState(
                context.Resource.Id,
                ResourceLifecycle.Stopped,
                observedEndpoints: member.Configuration.ObservedEndpoints);
            Remove(member);
            removed = true;
        }
        finally
        {
            member.Gate.Release();
            if (removed)
            {
                member.Dispose();
            }
        }
    }

    private async Task StartGenerationAsync(
        InProcessMember member,
        CancellationToken cancellationToken)
    {
        InProcessMemberConfiguration configuration = member.Configuration;
        configuration.Control.State.SetState(
            configuration.Control.Resource.Id,
            ResourceLifecycle.Starting,
            observedEndpoints: configuration.ObservedEndpoints);

        using var readinessTimeout = new CancellationTokenSource(
            _options.ReadinessBudget,
            _options.TimeProvider);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            readinessTimeout.Token);
        try
        {
            IResourceEntryInvocation invocation = _entries.Invoke(
                configuration.Artifact,
                configuration.ResourceContext);

            IHost child;
            try
            {
                child = await invocation.HostReady
                    .WaitAsync(readiness.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (readiness.Token.IsCancellationRequested)
            {
                _ = ProcessHost.ObserveAndDisposeUnadoptedAsync(
                    invocation,
                    configuration.ResourceContext);
                throw;
            }

            ProcessHostLease lease = await _host
                .AddAsync(
                    child,
                    invocation,
                    configuration.ResourceContext,
                    readiness.Token)
                .ConfigureAwait(false);
            member.Generation = new InProcessGeneration(lease);
        }
        catch (OperationCanceledException exception) when (
            readinessTimeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Resource '{configuration.Control.Resource.Name}' exceeded its in-process host "
                + $"adoption budget of '{_options.ReadinessBudget}'.",
                exception);
        }
    }

    private async Task CompleteReadinessAsync(
        InProcessMember member,
        CancellationToken cancellationToken)
    {
        await WaitForProbeAsync(
            member,
            member.Configuration.StartupProbe,
            cancellationToken).ConfigureAwait(false);
        await WaitForProbeAsync(
            member,
            member.Configuration.ReadinessProbe,
            cancellationToken).ConfigureAwait(false);
        InProcessMemberConfiguration configuration = member.Configuration;
        configuration.Control.State.SetState(
            configuration.Control.Resource.Id,
            ResourceLifecycle.Running,
            observedEndpoints: configuration.ObservedEndpoints);
    }

    private async Task CompleteReadinessWithinBudgetAsync(
        InProcessMember member,
        CancellationToken cancellationToken)
    {
        using var readinessTimeout = new CancellationTokenSource(
            _options.ReadinessBudget,
            _options.TimeProvider);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            readinessTimeout.Token);
        try
        {
            await CompleteReadinessAsync(member, readiness.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            readinessTimeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new InProcessReadinessTimeoutException(
                member.Configuration.Control.Resource.Name,
                _options.ReadinessBudget,
                exception);
        }
    }

    private async Task WaitForProbeAsync(
        InProcessMember member,
        InProcessProbeConfiguration probe,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            InProcessGeneration generation = member.Generation
                ?? throw new InvalidOperationException("The in-process generation was not adopted.");
            if (generation.Lease.Completion.IsCompleted)
            {
                await generation.Lease.Completion.ConfigureAwait(false);
                throw new InProcessMemberExitedException();
            }

            InProcessProbeResult result = await _probes
                .RunAsync(
                    probe,
                    member.Configuration.ResourceContext,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                return;
            }
            if (result.FailFast)
            {
                throw new InvalidOperationException(result.Detail);
            }

            await Task.Delay(
                _options.ProbeInterval,
                _options.TimeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(InProcessMember member)
    {
        CancellationToken cancellationToken = member.Lifetime.Token;
        try
        {
            if (!await CompleteReadinessOrRestartAsync(member, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            while (true)
            {
                await Task.Delay(
                    _options.ProbeInterval,
                    _options.TimeProvider,
                    cancellationToken).ConfigureAwait(false);

                InProcessGeneration generation = member.Generation
                    ?? throw new InvalidOperationException("The in-process generation is missing.");
                if (generation.Lease.Completion.IsCompleted)
                {
                    Exception? failure = null;
                    int exitCode = successExitCode;
                    try
                    {
                        await generation.Lease.Completion.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        exitCode = GetExitCode(exception, hasReachedReadiness: true);
                    }

                    string detail = failure is null
                        ? $"Resource '{member.Configuration.Control.Resource.Name}' exited cleanly."
                        : DescribeFailure(member, exitCode, failure);
                    if (!ShouldRestart(exitCode, member.Configuration.RestartPolicy))
                    {
                        await FinalizeGenerationAsync(member, exitCode, detail)
                            .ConfigureAwait(false);
                        return;
                    }

                    member.Configuration.Control.State.SetState(
                        member.Configuration.Control.Resource.Id,
                        ResourceLifecycle.Degraded,
                        detail,
                        member.Configuration.ObservedEndpoints);
                    if (!await RestartAsync(member, detail, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }

                InProcessProbeResult result;
                result = await _probes
                    .RunAsync(
                        member.Configuration.LivenessProbe,
                        member.Configuration.ResourceContext,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.Succeeded)
                {
                    member.ConsecutiveLivenessFailures = 0;
                    if (member.Configuration.Control.State.GetState(
                        member.Configuration.Control.Resource.Id) is ResourceLifecycle.Degraded)
                    {
                        member.Configuration.Control.State.SetState(
                            member.Configuration.Control.Resource.Id,
                            ResourceLifecycle.Running,
                            observedEndpoints: member.Configuration.ObservedEndpoints);
                    }
                    continue;
                }

                member.ConsecutiveLivenessFailures++;
                member.Configuration.Control.State.SetState(
                    member.Configuration.Control.Resource.Id,
                    ResourceLifecycle.Degraded,
                    result.Detail,
                    member.Configuration.ObservedEndpoints);
                if (member.ConsecutiveLivenessFailures < _options.LivenessFailureThreshold
                    || member.Configuration.RestartPolicy is RestartPolicy.Never)
                {
                    continue;
                }

                if (!await RestartAsync(member, result.Detail, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the gateway stops this member.
        }
        catch (Exception exception)
        {
            Exception? cleanupFailure = await ReleaseGenerationAsync(member)
                .ConfigureAwait(false);
            int exitCode = GetExitCode(
                exception,
                member.Configuration.Control.State.GetState(
                    member.Configuration.Control.Resource.Id)
                    is ResourceLifecycle.Running or ResourceLifecycle.Degraded);
            string detail = DescribeFailure(member, exitCode, exception);
            if (cleanupFailure is not null)
            {
                detail += $" Host cleanup failed: {cleanupFailure.Message}";
            }
            member.Configuration.Control.State.SetState(
                member.Configuration.Control.Resource.Id,
                ResourceLifecycle.Failed,
                detail,
                member.Configuration.ObservedEndpoints);
        }
    }

    private async Task<bool> CompleteReadinessOrRestartAsync(
        InProcessMember member,
        CancellationToken cancellationToken)
    {
        try
        {
            await CompleteReadinessWithinBudgetAsync(member, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            int exitCode = GetExitCode(exception, hasReachedReadiness: false);
            string detail = DescribeFailure(member, exitCode, exception);
            if (!ShouldRestart(exitCode, member.Configuration.RestartPolicy))
            {
                await FinalizeGenerationAsync(member, exitCode, detail).ConfigureAwait(false);
                return false;
            }

            member.Configuration.Control.State.SetState(
                member.Configuration.Control.Resource.Id,
                ResourceLifecycle.Degraded,
                detail,
                member.Configuration.ObservedEndpoints);
            return await RestartAsync(member, detail, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Exception?> ReleaseGenerationAsync(InProcessMember member)
    {
        await member.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await StopGenerationCapturingAsync(
                member,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            member.Gate.Release();
        }
    }

    private async Task FinalizeGenerationAsync(
        InProcessMember member,
        int exitCode,
        string detail)
    {
        Exception? cleanupFailure = await ReleaseGenerationAsync(member).ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            member.Configuration.Control.State.SetState(
                member.Configuration.Control.Resource.Id,
                ResourceLifecycle.Failed,
                $"{detail} Host cleanup failed: {cleanupFailure.Message}",
                member.Configuration.ObservedEndpoints);
            return;
        }

        SetTerminalState(member, exitCode, detail);
    }

    private async Task<bool> RestartAsync(
        InProcessMember member,
        string detail,
        CancellationToken cancellationToken)
    {
        await member.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                if (member.Generation is not null)
                {
                    member.Configuration.Control.State.SetState(
                        member.Configuration.Control.Resource.Id,
                        ResourceLifecycle.Stopping,
                        detail,
                        member.Configuration.ObservedEndpoints);
                }

                Exception? cleanupFailure = await StopGenerationCapturingAsync(
                    member,
                    cancellationToken).ConfigureAwait(false);
                if (cleanupFailure is not null)
                {
                    member.Configuration.Control.State.SetState(
                        member.Configuration.Control.Resource.Id,
                        ResourceLifecycle.Failed,
                        $"{detail} Host cleanup failed: {cleanupFailure.Message}",
                        member.Configuration.ObservedEndpoints);
                    return false;
                }

                if (!await WaitForRestartAttemptAsync(member, detail, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return false;
                }

                member.ConsecutiveLivenessFailures = 0;
                try
                {
                    await StartGenerationAsync(member, cancellationToken).ConfigureAwait(false);
                    await CompleteReadinessWithinBudgetAsync(member, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    int exitCode = GetExitCode(exception, hasReachedReadiness: false);
                    detail = DescribeFailure(member, exitCode, exception);
                    if (!ShouldRestart(exitCode, member.Configuration.RestartPolicy))
                    {
                        cleanupFailure = await StopGenerationCapturingAsync(
                            member,
                            CancellationToken.None).ConfigureAwait(false);
                        if (cleanupFailure is not null)
                        {
                            detail += $" Host cleanup failed: {cleanupFailure.Message}";
                        }
                        SetTerminalState(member, exitCode, detail);
                        return false;
                    }

                    member.Configuration.Control.State.SetState(
                        member.Configuration.Control.Resource.Id,
                        ResourceLifecycle.Degraded,
                        detail,
                        member.Configuration.ObservedEndpoints);
                }
            }
        }
        finally
        {
            member.Gate.Release();
        }
    }

    private async Task<bool> WaitForRestartAttemptAsync(
        InProcessMember member,
        string detail,
        CancellationToken cancellationToken)
    {
        int restartAttempt = ++member.RestartAttempts;
        if (restartAttempt > _options.MaximumRestartAttempts)
        {
            member.Configuration.Control.State.SetState(
                member.Configuration.Control.Resource.Id,
                ResourceLifecycle.Failed,
                $"Failed(restartable): resource '{member.Configuration.Control.Resource.Name}' "
                + $"exhausted restart limit {_options.MaximumRestartAttempts}. Last failure: {detail}",
                member.Configuration.ObservedEndpoints);
            return false;
        }

        TimeSpan backoff = CalculateRestartBackoff(
            restartAttempt,
            _options.InitialRestartBackoff,
            _options.MaximumRestartBackoff);
        await Task.Delay(backoff, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
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

    private async Task StopGenerationAsync(
        InProcessMember member,
        CancellationToken cancellationToken)
    {
        InProcessGeneration? generation = member.Generation;
        if (generation is null)
        {
            return;
        }

        try
        {
            await _host.RemoveAsync(
                generation.Lease,
                TimeSpan.FromSeconds(member.Configuration.Control.Plan.Workload.StopGraceSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // ProcessHost releases the lease from its ownership set even when stop, join, or
            // disposal reports a failure. Clear the generation only after that release attempt
            // completes so a later reconcile never retries teardown through a stale lease.
            member.Generation = null;
        }
    }

    private async Task<Exception?> StopGenerationCapturingAsync(
        InProcessMember member,
        CancellationToken cancellationToken)
    {
        try
        {
            await StopGenerationAsync(member, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static int GetExitCode(Exception exception, bool hasReachedReadiness)
    {
        if (exception is InProcessReadinessTimeoutException)
        {
            return runtimeExitCode;
        }

        if (exception is InProcessMemberExitedException)
        {
            return successExitCode;
        }

        ResourceEntryExitException? entryExit = FindEntryExitException(exception);
        return entryExit?.ExitCode
            ?? (hasReachedReadiness ? runtimeExitCode : startupExitCode);
    }

    private static ResourceEntryExitException? FindEntryExitException(Exception exception)
    {
        if (exception is ResourceEntryExitException entryExit)
        {
            return entryExit;
        }

        ResourceEntryExitException? selected = null;
        if (exception is AggregateException aggregateException)
        {
            foreach (Exception innerException in aggregateException.Flatten().InnerExceptions)
            {
                ResourceEntryExitException? candidate = FindEntryExitException(innerException);
                if (candidate?.ExitCode is configurationExitCode)
                {
                    return candidate;
                }
                selected ??= candidate;
            }
            if (selected is not null)
            {
                return selected;
            }
        }

        return exception.InnerException is null
            ? null
            : FindEntryExitException(exception.InnerException);
    }

    private static bool ShouldRestart(int exitCode, RestartPolicy policy)
    {
        return exitCode switch
        {
            successExitCode => policy is RestartPolicy.Always,
            dependencyExitCode or runtimeExitCode =>
                policy is RestartPolicy.OnFailure or RestartPolicy.Always,
            _ => false,
        };
    }

    private static string DescribeFailure(
        InProcessMember member,
        int exitCode,
        Exception exception)
    {
        if (exitCode is successExitCode)
        {
            return $"Resource '{member.Configuration.Control.Resource.Name}' exited cleanly before readiness.";
        }

        if (exception is InProcessReadinessTimeoutException)
        {
            return exception.Message;
        }

        ResourceEntryExitException? entryExit = FindEntryExitException(exception);
        Exception detail = entryExit?.InnerException ?? exception;
        return $"Resource '{member.Configuration.Control.Resource.Name}' exited with code {exitCode}: {detail.Message}";
    }

    private static void SetTerminalState(
        InProcessMember member,
        int exitCode,
        string detail)
    {
        ResourceLifecycle state = exitCode is successExitCode
            or interruptedExitCode
            or terminatedExitCode
                ? ResourceLifecycle.Stopped
                : ResourceLifecycle.Failed;
        string classifiedDetail = exitCode switch
        {
            configurationExitCode or startupExitCode => $"Failed(final; exit {exitCode}): {detail}",
            dependencyExitCode or runtimeExitCode =>
                $"Failed(restartable; policy {member.Configuration.RestartPolicy}; exit {exitCode}): {detail}",
            _ => detail,
        };
        member.Configuration.Control.State.SetState(
            member.Configuration.Control.Resource.Id,
            state,
            classifiedDetail,
            member.Configuration.ObservedEndpoints);
    }

    private void Remove(InProcessMember member)
    {
        lock (_lock)
        {
            if (_members.TryGetValue(
                member.Key,
                out InProcessMember? current)
                && ReferenceEquals(current, member))
            {
                _members.Remove(member.Key);
            }
        }
    }
}

internal sealed class InProcessMember : IDisposable
{
    internal InProcessMember(
        InProcessMemberKey key,
        InProcessMemberConfiguration configuration)
    {
        Key = key;
        Configuration = configuration;
    }

    internal InProcessMemberKey Key { get; }

    internal InProcessMemberConfiguration Configuration { get; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal CancellationTokenSource Lifetime { get; } = new();

    internal InProcessGeneration? Generation { get; set; }

    internal Task? Monitor { get; set; }

    internal int ConsecutiveLivenessFailures { get; set; }

    internal int RestartAttempts { get; set; }

    public void Dispose()
    {
        Lifetime.Dispose();
        Gate.Dispose();
    }
}

internal readonly record struct InProcessMemberKey(
    ApplicationName Application,
    ResourceId Resource);

internal sealed record InProcessGeneration(ProcessHostLease Lease);

internal sealed class InProcessMemberExitedException : Exception;

internal sealed class InProcessReadinessTimeoutException : TimeoutException
{
    internal InProcessReadinessTimeoutException(
        ResourceName resourceName,
        TimeSpan readinessBudget,
        Exception innerException)
        : base(
            $"Resource '{resourceName}' exceeded its in-process readiness budget of "
            + $"'{readinessBudget}'.",
            innerException)
    {
    }
}
