using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Provides the ambient context and assembly-keyed default control-plane registrations for
/// Cohesion resource invocations.
/// </summary>
public static class ResourceRuntime
{
    private static readonly AsyncLocal<ResourceContextFrame?> AmbientContext = new();
    private static readonly ConcurrentDictionary<Assembly, ControlPlaneRegistration> ControlPlanes = new();
    private static readonly ConcurrentDictionary<Assembly, byte> Entries = new();
    private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, string>> EndpointCertificates = new();
    private static readonly ConditionalWeakTable<IHost, IResourceControlPlane> HostControlPlanes = new();

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
            AmbientContext.Value = new ResourceContextFrame(
                context,
                isExplicitInvocationScope: false);
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
        var installed = new ResourceContextFrame(
            context,
            isExplicitInvocationScope: true);
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
    /// Registers an enabled resource assembly's compiler-rooted executable entry point.
    /// </summary>
    /// <param name="assembly">The resource executable assembly.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The assembly has no entry point or already has an entry registration.
    /// </exception>
    public static void RegisterEntry(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (assembly.EntryPoint is null)
        {
            throw new InvalidOperationException(
                $"Resource assembly '{assembly.GetName().Name}' does not have an executable entry point.");
        }

        if (!Entries.TryAdd(assembly, 0))
        {
            throw new InvalidOperationException(
                $"Resource assembly '{assembly.GetName().Name}' already registered its entry point.");
        }
    }

    /// <summary>
    /// Determines whether an enabled resource assembly registered its executable entry point.
    /// </summary>
    /// <param name="assembly">The resource executable assembly.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="assembly"/> has an entry registration;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    public static bool IsEntryRegistered(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return Entries.ContainsKey(assembly);
    }

    /// <summary>Registers immutable endpoint-to-certificate-mount metadata emitted for an enabled executable.</summary>
    /// <param name="assembly">The resource executable assembly.</param>
    /// <param name="endpointCertificates">Endpoint names and their Secret mount names or reserved public literal.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The assembly already registered its certificate metadata.</exception>
    public static void RegisterEndpointCertificates(Assembly assembly, IReadOnlyDictionary<string, string> endpointCertificates)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(endpointCertificates);
        if (!EndpointCertificates.TryAdd(assembly, new Dictionary<string, string>(endpointCertificates, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The resource assembly already registered its endpoint certificate mounts.");
        }
    }

    /// <summary>
    /// Invokes an enabled resource assembly's registered entry point in the current ambient
    /// resource scope.
    /// </summary>
    /// <param name="assembly">The resource executable assembly.</param>
    /// <param name="args">The command-line arguments passed to the executable.</param>
    /// <returns>
    /// An invocation whose host-ready task completes after the area builder surrenders its host and
    /// invokes its run pipeline, or exits successfully with the host still idle. The completion task
    /// represents the executable's full lifetime.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="assembly"/> or <paramref name="args"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The assembly is not an enabled resource or the current ambient scope already owns an entry
    /// invocation.
    /// </exception>
    [RequiresUnreferencedCode(
        "Invoking Assembly.EntryPoint requires the executable entry point to be rooted by generated metadata.")]
    public static IResourceEntryInvocation InvokeEntry(Assembly assembly, string[] args)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(args);

        if (!Entries.ContainsKey(assembly))
        {
            throw new InvalidOperationException(
                $"Resource assembly '{assembly.GetName().Name}' did not register an enabled resource entry point.");
        }

        return InvokeEntryPointCore(assembly, args);
    }

    /// <summary>
    /// Invokes a compiler-rooted executable entry point directly in the current ambient resource
    /// scope without requiring an entry registration.
    /// </summary>
    /// <param name="assembly">The resource executable assembly.</param>
    /// <param name="args">The command-line arguments passed to the executable.</param>
    /// <returns>
    /// An invocation whose host-ready task completes after the area builder surrenders its host and
    /// invokes its run pipeline, or exits successfully with the host still idle. The completion task
    /// represents the executable's full lifetime.
    /// </returns>
    /// <remarks>
    /// This fallback is for callers that already hold a compiler-rooted reference to the executable
    /// assembly and establish its resource eligibility independently. Generated registration remains
    /// the normal enabled-resource path exposed by <see cref="InvokeEntry(Assembly, string[])"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="assembly"/> or <paramref name="args"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The assembly has no executable entry point, the current ambient scope is not an explicit
    /// invocation scope, or that scope already owns an entry invocation.
    /// </exception>
    [RequiresUnreferencedCode(
        "Invoking Assembly.EntryPoint requires the executable entry point to be rooted by the caller.")]
    public static IResourceEntryInvocation InvokeEntryPoint(Assembly assembly, string[] args)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(args);

        if (assembly.EntryPoint is null)
        {
            throw new InvalidOperationException(
                $"Resource assembly '{assembly.GetName().Name}' does not have an executable entry point.");
        }

        return InvokeEntryPointCore(assembly, args);
    }

    private static IResourceEntryInvocation InvokeEntryPointCore(Assembly assembly, string[] args)
    {
        ResourceContextFrame? frame = AmbientContext.Value;
        if (frame is null || !frame.IsExplicitInvocationScope)
        {
            throw new InvalidOperationException(
                "A resource entry point must be invoked inside a ResourceRuntime.CreateScope scope.");
        }
        TaskCompletionSource<IHost> hostReady = frame.PrepareEntryInvocation(assembly);
        string[] invocationArguments = (string[])args.Clone();

        Task completion = Task.Factory.StartNew(
            () => InvokeEntryCoreAsync(assembly, invocationArguments),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        _ = ObserveEntryCompletionAsync(assembly, completion, hostReady, frame);
        return new ResourceEntryInvocation(hostReady.Task, completion);
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
    /// <param name="assembly">
    /// The process entry assembly used for standalone execution. An active entry invocation's
    /// logical resource assembly takes precedence.
    /// </param>
    /// <param name="controlPlane">The new isolated control plane when registered.</param>
    /// <returns>True when the assembly registered a control plane; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    public static bool TryCreateControlPlane(
        Assembly assembly,
        out IResourceControlPlane? controlPlane)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        // An in-process member executes beneath the gateway's process entry assembly. The
        // invocation frame is the logical resource caller and must win over that process-wide
        // identity so concurrent members resolve only their own registered control plane.
        Assembly registrationAssembly = AmbientContext.Value?.EntryAssembly ?? assembly;

        if (EndpointCertificates.TryGetValue(registrationAssembly, out IReadOnlyDictionary<string, string>? certificates))
        {
            Current.SetEndpointCertificates(certificates);
        }

        if (!ControlPlanes.TryGetValue(
            registrationAssembly,
            out ControlPlaneRegistration? registration))
        {
            controlPlane = null;
            return false;
        }

        IResourceControlPlane inner = registration.Factory.Invoke()
            ?? throw new InvalidOperationException(
                $"Resource assembly '{registrationAssembly.GetName().Name}' returned a null default control plane.");
        controlPlane = new RegisteredResourceControlPlane(inner, registration.StopGraceSeconds);
        return true;
    }

    /// <summary>
    /// Attaches a registered control plane to its built host and installs resource-host lifecycle
    /// behavior for that host only.
    /// </summary>
    /// <param name="host">The built area host.</param>
    /// <param name="controlPlane">The invocation's control plane.</param>
    /// <remarks>
    /// During an explicit entry invocation this records the surrendered host. Host readiness is
    /// published only when the host's run pipeline is subsequently invoked, or when the entry point
    /// exits successfully while the host remains idle for parent-owned startup.
    /// </remarks>
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
        ResourceContextFrame? frame = AmbientContext.Value;
        bool isEntryInvocationHost = frame?.HostBuilt(host) is true;
        foreach ((string name, Uri endpoint) in context.Endpoints)
        {
            controlPlane.ObserveEndpoint(name, endpoint);
        }
        controlPlane.AttachHost(host);
        HostControlPlanes.AddOrUpdate(host, controlPlane);

        int stopGraceSeconds = controlPlane is RegisteredResourceControlPlane registered
            ? registered.StopGraceSeconds
            : ResourceHostOptions.DefaultStopGraceSeconds;
        hostContext.Runner = new ResourceHostRunner(new ResourceHostOptions(
            stopGraceSeconds,
            contentRootPath: context.ContentRootPath,
            stopEventName: context.GetEnvironmentValue(ResourceEnvironment.StopEvent),
            runMode: isEntryInvocationHost
                ? ResourceHostRunMode.InProcess
                : ResourceHostRunMode.Process,
            runInvoked: isEntryInvocationHost
                ? () => frame!.RunInvoked(host)
                : null));
    }

    /// <summary>Finds the invocation-local control plane attached to a built host.</summary>
    /// <param name="host">The host surrendered by its resource executable.</param>
    /// <param name="controlPlane">The attached control plane, if present.</param>
    /// <returns>Whether a resource control plane was attached to the host.</returns>
    /// <exception cref="ArgumentNullException">The host is null.</exception>
    public static bool TryGetControlPlane(IHost host, [NotNullWhen(true)] out IResourceControlPlane? controlPlane)
    {
        ArgumentNullException.ThrowIfNull(host);
        return HostControlPlanes.TryGetValue(host, out controlPlane);
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
        private TaskCompletionSource<IHost>? _entryHostReady;
        private Assembly? _entryAssembly;
        private IHost? _entryHost;

        internal ResourceContextFrame(
            ResourceContext context,
            bool isExplicitInvocationScope)
        {
            Context = context;
            IsExplicitInvocationScope = isExplicitInvocationScope;
        }

        internal ResourceContext Context { get; }

        internal bool IsExplicitInvocationScope { get; }

        internal Assembly? EntryAssembly
        {
            get
            {
                lock (this)
                {
                    return _entryAssembly;
                }
            }
        }

        internal TaskCompletionSource<IHost> PrepareEntryInvocation(Assembly assembly)
        {
            lock (this)
            {
                if (_entryHostReady is not null)
                {
                    throw new InvalidOperationException(
                        $"Resource scope already owns an invocation of '{_entryAssembly!.GetName().Name}'.");
                }

                _entryAssembly = assembly;
                _entryHostReady = new TaskCompletionSource<IHost>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _entryHostReady;
            }
        }

        internal bool HostBuilt(IHost host)
        {
            lock (this)
            {
                if (_entryHostReady is null)
                {
                    return false;
                }
                if (_entryHost is null)
                {
                    _entryHost = host;
                    return true;
                }
                if (!ReferenceEquals(_entryHost, host))
                {
                    throw new InvalidOperationException(
                        $"Resource entry invocation '{_entryAssembly!.GetName().Name}' surrendered more than one host.");
                }

                return true;
            }
        }

        internal void RunInvoked(IHost host)
        {
            lock (this)
            {
                if (ReferenceEquals(_entryHost, host))
                {
                    _entryHostReady?.TrySetResult(host);
                }
            }
        }

        internal bool EntryPointCompleted()
        {
            lock (this)
            {
                if (_entryHost is not null && _entryHost.Context.State is HostState.Idle)
                {
                    _entryHostReady?.TrySetResult(_entryHost);
                }
                return _entryHostReady?.Task.IsCompleted is true;
            }
        }

        internal void EntryPointFailed(Exception exception)
        {
            lock (this)
            {
                if (_entryHost is not null)
                {
                    // A surrendered host remains caller-owned even when composition fails
                    // before RunAsync. Completion retains the original failure while HostReady
                    // gives ProcessHost the exact instance it must dispose.
                    _entryHostReady?.TrySetResult(_entryHost);
                    return;
                }

                _entryHostReady?.TrySetException(exception);
            }
        }
    }

    private static async Task ObserveEntryCompletionAsync(
        Assembly assembly,
        Task completion,
        TaskCompletionSource<IHost> hostReady,
        ResourceContextFrame frame)
    {
        try
        {
            await completion.ConfigureAwait(false);
            if (!frame.EntryPointCompleted())
            {
                hostReady.TrySetException(new InvalidOperationException(
                    $"Resource entry point '{assembly.GetName().Name}' exited before surrendering a runnable host."));
            }
        }
        catch (Exception exception)
        {
            frame.EntryPointFailed(exception);
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "The executable entry point is compiler-rooted, including top-level statements and explicitly named Main methods.")]
    private static async Task InvokeEntryCoreAsync(Assembly assembly, string[] args)
    {
        MethodInfo entryPoint = assembly.EntryPoint
            ?? throw new InvalidOperationException(
                $"Resource assembly '{assembly.GetName().Name}' does not have an executable entry point.");
        ParameterInfo[] parameters = entryPoint.GetParameters();
        object?[]? invocationArguments = parameters.Length switch
        {
            0 => null,
            1 when parameters[0].ParameterType == typeof(string[]) => new object?[] { args },
            _ => throw new InvalidOperationException(
                $"Resource entry point '{assembly.GetName().Name}' has an unsupported signature."),
        };

        object? result;
        try
        {
            result = entryPoint.Invoke(null, invocationArguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        int exitCode = result switch
        {
            Task<int> exitTask => await exitTask.ConfigureAwait(false),
            Task task => await AwaitEntryTaskAsync(task).ConfigureAwait(false),
            int code => code,
            _ => 0,
        };

        if (exitCode != 0)
        {
            throw new ResourceEntryExitException(exitCode);
        }
    }

    private static async Task<int> AwaitEntryTaskAsync(Task task)
    {
        await task.ConfigureAwait(false);
        return 0;
    }

    private sealed record ControlPlaneRegistration(
        Func<IResourceControlPlane> Factory,
        int StopGraceSeconds);

    private sealed record ResourceEntryInvocation(
        Task<IHost> HostReady,
        Task Completion) : IResourceEntryInvocation;

    private sealed class RegisteredResourceControlPlane : IResourceControlPlane
    {
        private readonly IResourceControlPlane _inner;

        internal RegisteredResourceControlPlane(IResourceControlPlane inner, int stopGraceSeconds)
        {
            _inner = inner;
            StopGraceSeconds = stopGraceSeconds;
        }

        public IReadOnlyList<string> AcceptedCommandKinds => _inner.AcceptedCommandKinds;

        public IReadOnlyList<ResourceCommand> Commands => _inner.Commands;

        public void RegisterCommandHandler(IResourceCommandHandler handler) => _inner.RegisterCommandHandler(handler);

        public ValueTask<ReadOnlyMemory<byte>> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
            _inner.DeleteCommandAsync(command, cancellationToken);

        public IReadOnlyDictionary<string, Uri> ObservedEndpoints => _inner.ObservedEndpoints;

        internal int StopGraceSeconds { get; }

        public void AddHealthContributor(IHealthContributor contributor) =>
            _inner.AddHealthContributor(contributor);

        public void ObserveEndpoint(string name, Uri address) =>
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
