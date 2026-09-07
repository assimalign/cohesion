using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Provides the ambient context and assembly-keyed default control-plane registrations for
/// Cohesion resource invocations.
/// </summary>
public static class ResourceRuntime
{
    private static readonly AsyncLocal<ResourceContextFrame?> AmbientContext = new();
    private static readonly ConcurrentDictionary<Assembly, ControlPlaneRegistration> ControlPlanes = new();

    /// <summary>
    /// Gets the current invocation context, lazily snapshotting the process environment in the
    /// current asynchronous flow when no in-process scope is active.
    /// </summary>
    public static ResourceContext Current
    {
        get
        {
            ResourceContextFrame? frame = AmbientContext.Value;
            if (frame is not null)
            {
                return frame.Context;
            }

            var context = ResourceContext.FromEnvironment();
            AmbientContext.Value = new ResourceContextFrame(context);
            return context;
        }
    }

    /// <summary>
    /// Installs an invocation context in the current asynchronous flow.
    /// </summary>
    /// <param name="context">The invocation context.</param>
    /// <returns>A scope that restores the preceding context when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public static IDisposable CreateScope(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceContextFrame? prior = AmbientContext.Value;
        var installed = new ResourceContextFrame(context);
        AmbientContext.Value = installed;
        return new ResourceContextScope(prior, installed);
    }

    /// <summary>
    /// Installs the area runtime's transport-factory resolver on the current invocation context.
    /// </summary>
    /// <param name="resolver">
    /// A protocol resolver that returns a connection factory, or null for an unsupported protocol.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is null.</exception>
    public static void RegisterConnectionFactoryResolver(Func<string, object?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        Current.SetConnectionFactoryResolver(resolver);
    }

    /// <summary>
    /// Registers an enabled resource assembly's area default control-plane factory.
    /// </summary>
    /// <param name="assembly">The resource executable assembly.</param>
    /// <param name="factory">A factory that creates one isolated control plane per builder.</param>
    /// <param name="stopGraceSeconds">The manifest-declared graceful-stop budget.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stopGraceSeconds"/> is less than the minimum shutdown budget.
    /// </exception>
    /// <exception cref="InvalidOperationException">The assembly already has a registration.</exception>
    public static void RegisterControlPlane(
        Assembly assembly,
        Func<IResourceControlPlane> factory,
        int stopGraceSeconds = ResourceHostOptions.DefaultStopGraceSeconds)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(factory);
        if (stopGraceSeconds < ResourceHostOptions.MinimumStopGraceSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopGraceSeconds),
                stopGraceSeconds,
                $"The stop grace period must be at least {ResourceHostOptions.MinimumStopGraceSeconds} seconds.");
        }

        if (!ControlPlanes.TryAdd(assembly, new ControlPlaneRegistration(factory, stopGraceSeconds)))
        {
            throw new InvalidOperationException(
                $"Resource assembly '{assembly.GetName().Name}' already registered a default control plane.");
        }
    }

    /// <summary>
    /// Attempts to create the default control plane registered by a resource assembly.
    /// </summary>
    /// <param name="assembly">The calling resource executable assembly.</param>
    /// <param name="controlPlane">The new isolated control plane when registered.</param>
    /// <returns>True when the assembly registered a control plane; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    public static bool TryCreateControlPlane(
        Assembly assembly,
        out IResourceControlPlane? controlPlane)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!ControlPlanes.TryGetValue(assembly, out ControlPlaneRegistration? registration))
        {
            controlPlane = null;
            return false;
        }

        IResourceControlPlane inner = registration.Factory.Invoke()
            ?? throw new InvalidOperationException(
                $"Resource assembly '{assembly.GetName().Name}' returned a null default control plane.");
        controlPlane = new RegisteredResourceControlPlane(inner, registration.StopGraceSeconds);
        return true;
    }

    /// <summary>
    /// Attaches a registered control plane to its built host and installs resource-host lifecycle
    /// behavior for that host only.
    /// </summary>
    /// <param name="host">The built area host.</param>
    /// <param name="controlPlane">The invocation's control plane.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="controlPlane"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="host"/> does not use a Cohesion <see cref="HostContext"/>.</exception>
    public static void HostBuilt(IHost host, IResourceControlPlane controlPlane)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(controlPlane);

        if (host.Context is not HostContext hostContext)
        {
            throw new InvalidOperationException("A Cohesion resource host must use HostContext.");
        }

        ResourceContext context = Current;
        foreach ((string name, EndpointAddress endpoint) in context.Endpoints)
        {
            controlPlane.ObserveEndpoint(name, endpoint);
        }
        controlPlane.AttachHost(host);

        int stopGraceSeconds = controlPlane is RegisteredResourceControlPlane registered
            ? registered.StopGraceSeconds
            : ResourceHostOptions.DefaultStopGraceSeconds;
        hostContext.ResourceHostOptions = new ResourceHostOptions(
            stopGraceSeconds,
            contentRootPath: context.ContentRootPath,
            stopEventName: context.GetEnvironmentValue(ResourceEnvironment.StopEvent));
    }

    private sealed class ResourceContextScope : IDisposable
    {
        private readonly ResourceContextFrame? _prior;
        private readonly ResourceContextFrame _installed;
        private int _disposed;

        internal ResourceContextScope(ResourceContextFrame? prior, ResourceContextFrame installed)
        {
            _prior = prior;
            _installed = installed;
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!ReferenceEquals(AmbientContext.Value, _installed))
            {
                throw new InvalidOperationException("Resource context scopes must be disposed in nesting order.");
            }

            AmbientContext.Value = _prior;
            _ = Interlocked.Exchange(ref _disposed, 1);
        }
    }

    private sealed class ResourceContextFrame
    {
        internal ResourceContextFrame(ResourceContext context)
        {
            Context = context;
        }

        internal ResourceContext Context { get; }
    }

    private sealed record ControlPlaneRegistration(
        Func<IResourceControlPlane> Factory,
        int StopGraceSeconds);

    private sealed class RegisteredResourceControlPlane : IResourceControlPlane
    {
        private readonly IResourceControlPlane _inner;

        internal RegisteredResourceControlPlane(IResourceControlPlane inner, int stopGraceSeconds)
        {
            _inner = inner;
            StopGraceSeconds = stopGraceSeconds;
        }

        public IReadOnlyList<string> AcceptedCommandKinds => _inner.AcceptedCommandKinds;

        public IReadOnlyDictionary<string, EndpointAddress> ObservedEndpoints => _inner.ObservedEndpoints;

        internal int StopGraceSeconds { get; }

        public void AddHealthContributor(IHealthContributor contributor) =>
            _inner.AddHealthContributor(contributor);

        public void ObserveEndpoint(string name, EndpointAddress address) =>
            _inner.ObserveEndpoint(name, address);

        public ValueTask<ResourceHealthReport> CheckHealthAsync(
            CancellationToken cancellationToken = default) =>
            _inner.CheckHealthAsync(cancellationToken);

        public ValueTask<ResourceHealthReport> CheckReadinessAsync(
            CancellationToken cancellationToken = default) =>
            _inner.CheckReadinessAsync(cancellationToken);

        public ValueTask<ResourceHealthReport> CheckLivenessAsync(
            CancellationToken cancellationToken = default) =>
            _inner.CheckLivenessAsync(cancellationToken);

        public void AttachHost(IHost host) => _inner.AttachHost(host);

        public ValueTask RequestStopAsync(CancellationToken cancellationToken = default) =>
            _inner.RequestStopAsync(cancellationToken);

        public ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
            ResourceCommand command,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteCommandAsync(command, cancellationToken);
    }
}
