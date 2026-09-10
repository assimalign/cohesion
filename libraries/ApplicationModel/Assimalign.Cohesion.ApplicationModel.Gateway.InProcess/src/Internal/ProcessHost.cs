using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Owns the nested host services surrendered by in-process resource entry invocations.
/// </summary>
internal sealed class ProcessHost : Host<ProcessHostContext>
{
    private readonly ProcessHostContext _context;
    private IDisposable? _consoleRouterLease;

    internal ProcessHost(string environmentName, string contentRootPath)
        : base(new ProcessHostOptions())
    {
        _context = new ProcessHostContext(environmentName, contentRootPath);
    }

    public override ProcessHostContext Context => _context;

    protected override Task OnStartingAsync(CancellationToken cancellationToken = default)
    {
        IDisposable lease = InProcessConsoleRouter.Acquire();
        if (Interlocked.CompareExchange(ref _consoleRouterLease, lease, comparand: null) is not null)
        {
            lease.Dispose();
            throw new InvalidOperationException(
                "The in-process console router is already active for this host run.");
        }

        return Task.CompletedTask;
    }

    internal async Task<ProcessHostLease> AddAsync(
        IHost child,
        IResourceEntryInvocation invocation,
        ResourceContext resourceContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(resourceContext);

        IHostService service = new AmbientResourceHostService(
            child.AsService(),
            resourceContext);
        bool parentOwnsLifetime = child.Context.State is HostState.Idle
            && invocation.Completion.IsCompletedSuccessfully;
        try
        {
            if (Context.State is not HostState.Started)
            {
                throw new InvalidOperationException(
                    "The in-process gateway host must be started before a member is added.");
            }

            await StartAdoptedServiceAsync(child, service, invocation.Completion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _ = ObserveAndDisposeUnadoptedAsync(
                child,
                service,
                invocation,
                resourceContext);
            throw;
        }

        Task completion = parentOwnsLifetime
            ? child.Context.WaitForShutdownAsync()
            : invocation.Completion;
        var lease = new ProcessHostLease(
            child,
            service,
            invocation,
            completion,
            resourceContext);
        Context.Add(lease);
        return lease;
    }

    internal static async Task ObserveAndDisposeUnadoptedAsync(
        IResourceEntryInvocation invocation,
        ResourceContext resourceContext)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(resourceContext);

        try
        {
            IHost host = await invocation.HostReady.ConfigureAwait(false);
            await DisposeUnadoptedAsync(
                host,
                new AmbientResourceHostService(host.AsService(), resourceContext),
                invocation,
                resourceContext).ConfigureAwait(false);
        }
        catch
        {
            // The invoking caller owns the cancellation or entry failure. This observer only
            // joins and disposes a host that is surrendered after that caller stops waiting.
        }
    }

    internal async Task RemoveAsync(
        ProcessHostLease lease,
        TimeSpan stopGrace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);

        using var grace = new CancellationTokenSource(stopGrace);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            grace.Token);

        List<Exception>? failures = null;
        try
        {
            await lease.Service.StopAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await lease.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception) when (lease.Completion.IsCompleted)
        {
            // The monitor owns entry-point failure classification. A completed fault does
            // not prevent deterministic host disposal and ownership release.
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await DisposeHostAsync(lease.Host, lease.ResourceContext).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            Context.Remove(lease);
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures is { Count: > 1 })
        {
            throw new AggregateException(
                "The in-process resource host could not be stopped and released cleanly.",
                failures);
        }
    }

    protected override async Task OnStoppedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ProcessHostLease[] remaining = Context.Drain();
            List<Exception>? failures = null;
            for (int index = remaining.Length - 1; index >= 0; index--)
            {
                ProcessHostLease lease = remaining[index];
                try
                {
                    await lease.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (lease.Completion.IsCompleted)
                {
                    // A completed entry failure is already reflected by the member monitor.
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }

                try
                {
                    await DisposeHostAsync(lease.Host, lease.ResourceContext).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            if (failures is not null)
            {
                throw new AggregateException(
                    "One or more in-process resource hosts could not be released.",
                    failures);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _consoleRouterLease, null)?.Dispose();
        }
    }

    private static async Task StartAdoptedServiceAsync(
        IHost host,
        IHostService service,
        Task program,
        CancellationToken cancellationToken)
    {
        // The resource runner releases HostReady only after RunAsync has claimed the member run.
        // Observe an in-flight start to Started; a successfully completed entry that surrendered
        // an Idle host deliberately transfers direct start/stop ownership to this parent.
        while (true)
        {
            while (host.Context.State is HostState.Starting)
            {
                Task delay = Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                Task completed = await Task.WhenAny(program, delay).ConfigureAwait(false);
                if (ReferenceEquals(completed, program))
                {
                    await program.ConfigureAwait(false);
                }
                await delay.ConfigureAwait(false);
            }

            if (host.Context.State is HostState.Failed or HostState.Stopping)
            {
                throw new InvalidOperationException(
                    $"Nested resource host '{host.Id}' entered '{host.Context.State}' before adoption.");
            }
            if (program.IsCompleted)
            {
                await program.ConfigureAwait(false);
                if (host.Context.State is not HostState.Idle)
                {
                    throw new InvalidOperationException(
                        $"Nested resource host '{host.Id}' exited before adoption.");
                }
            }

            try
            {
                await service.StartAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HostStartupException) when (host.Context.State is HostState.Starting)
            {
                // Program won the Idle-to-Starting race after the state check. Let its
                // owned runner finish startup, then have the wrapper observe Started.
            }
        }
    }

    private static async Task DisposeUnadoptedAsync(
        IHost host,
        IHostService service,
        IResourceEntryInvocation invocation,
        ResourceContext resourceContext)
    {
        try
        {
            await StartAdoptedServiceAsync(
                host,
                service,
                invocation.Completion,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Startup failure is already retained by the entry invocation or AddAsync caller.
        }

        try
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup remains best effort so disposal cannot be skipped after a failed adoption.
        }

        try
        {
            await invocation.Completion.ConfigureAwait(false);
        }
        catch
        {
            // The original caller retains the entry failure.
        }

        await DisposeHostAsync(host, resourceContext).ConfigureAwait(false);
    }

    private static async Task ObserveAndDisposeUnadoptedAsync(
        IHost host,
        IHostService service,
        IResourceEntryInvocation invocation,
        ResourceContext resourceContext)
    {
        try
        {
            await DisposeUnadoptedAsync(
                host,
                service,
                invocation,
                resourceContext).ConfigureAwait(false);
        }
        catch
        {
            // The failed AddAsync caller retains the adoption failure. This observer only
            // completes deferred cleanup after a cancellation-ignoring child settles.
        }
    }

    private static async ValueTask DisposeHostAsync(
        IHost host,
        ResourceContext resourceContext)
    {
        using IDisposable scope = ResourceRuntime.CreateScope(resourceContext);
        await host.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ProcessHostContext : HostContext
{
    private readonly Lock _lock = new();
    private readonly List<ProcessHostLease> _leases = [];
    private readonly HostEnvironment _environment;

    internal ProcessHostContext(string environmentName, string contentRootPath)
    {
        _environment = new HostEnvironment(environmentName)
        {
            ContentRootPath = contentRootPath,
        };
    }

    public override IHostEnvironment Environment => _environment;

    public override IEnumerable<IHostService> HostedServices
    {
        get
        {
            lock (_lock)
            {
                var services = new IHostService[_leases.Count];
                for (int index = 0; index < services.Length; index++)
                {
                    services[index] = _leases[index].Service;
                }
                return services;
            }
        }
    }

    internal void Add(ProcessHostLease lease)
    {
        lock (_lock)
        {
            _leases.Add(lease);
        }
    }

    internal void Remove(ProcessHostLease lease)
    {
        lock (_lock)
        {
            _leases.Remove(lease);
        }
    }

    internal ProcessHostLease[] Drain()
    {
        lock (_lock)
        {
            ProcessHostLease[] result = _leases.ToArray();
            _leases.Clear();
            return result;
        }
    }
}

internal sealed class ProcessHostOptions : HostOptions<ProcessHostContext>;

internal sealed record ProcessHostLease(
    IHost Host,
    IHostService Service,
    IResourceEntryInvocation Invocation,
    Task Completion,
    ResourceContext ResourceContext);

internal sealed class AmbientResourceHostService : IHostService
{
    private readonly IHostService _inner;
    private readonly ResourceContext _context;

    internal AmbientResourceHostService(IHostService inner, ResourceContext context)
    {
        _inner = inner;
        _context = context;
    }

    public ServiceId Id => _inner.Id;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable scope = ResourceRuntime.CreateScope(_context);
        await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable scope = ResourceRuntime.CreateScope(_context);
        await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
