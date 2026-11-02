using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Declares a gateway's application and configures the gateway that runs it, then builds the
/// gateway: the builder a gateway program uses.
/// </summary>
/// <remarks>
/// <para>
/// It implements <see cref="IApplicationModelBuilder"/> and <see cref="IApplicationGatewayBuilder"/>
/// side by side. The model half is a model builder of
/// <see cref="ApplicationModel.CreateBuilder(GatewayName)"/>, so a model follows the same rules
/// however it is built, and an area's registration verbs reach it through either interface. A
/// resource definition's <see cref="IApplicationResourceDefinition.Set"/> is that model builder: it
/// composes the same set, but it is not this object.
/// <see cref="ApplicationGateway.CreateBuilder(GatewayName)"/> creates one.
/// </para>
/// <para>
/// <see cref="Build"/> builds the model, unless <see cref="IApplicationModelBuilder.Build"/> already
/// did, and the gateway that runs it. A builder is composed on one thread and is not safe for
/// concurrent changes.
/// </para>
/// <para>
/// A platform reaches the gateway in one of two ways. <see cref="UsePlatform{TPlatform}"/> takes a
/// factory that <see cref="Build"/> calls, and the gateway owns what it creates: disposing the gateway
/// disposes it. <see cref="UseInfrastructure"/> and <see cref="UseInstanceClient"/> take objects the
/// caller created, which the gateway borrows and never disposes. Each call sets the roles it names,
/// and the last call for a role wins: a platform is both the infrastructure and the instance client,
/// so <see cref="UseInstanceClient"/> after <see cref="UsePlatform{TPlatform}"/> keeps the platform as
/// the infrastructure only. A factory whose platform no longer fills either role is not called.
/// </para>
/// </remarks>
[DebuggerDisplay("{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ApplicationGatewayBuilder : IApplicationModelBuilder, IApplicationGatewayBuilder
{
    private static readonly TimeSpan _defaultStartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _defaultLeaseDuration = TimeSpan.FromSeconds(30);

    private readonly IApplicationModelBuilder _model;
    private IApplicationGatewayInfrastructureProvider? _infrastructure;
    private IApplicationGatewayStore? _store;
    private IApplicationGatewayInstanceClient? _client;

    // Fills whichever of the two roles no borrowed object fills; one object fills both.
    private Func<(IApplicationGatewayInfrastructureProvider Infrastructure, IApplicationGatewayInstanceClient Client)>? _platform;
    private IApplicationGatewayParent? _parent;
    private IApplicationModel? _built;
    private TimeSpan _startupTimeout = _defaultStartupTimeout;
    private TimeSpan _leaseDuration = _defaultLeaseDuration;
    private bool _isBuilt;

    internal ApplicationGatewayBuilder(GatewayName name)
    {
        _model = ApplicationModel.CreateBuilder(name);
    }

    /// <inheritdoc/>
    public GatewayName Name => _model.Name;

    /// <inheritdoc/>
    public IApplicationModelParent? Parent => _model.Parent;

    /// <inheritdoc/>
    public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _model.Resources;

    /// <inheritdoc/>
    public TimeSpan StartupTimeout
    {
        get => _startupTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ThrowIfBuilt();
            _startupTimeout = value;
        }
    }

    /// <inheritdoc/>
    public TimeSpan LeaseDuration
    {
        get => _leaseDuration;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ThrowIfBuilt();
            _leaseDuration = value;
        }
    }

    /// <inheritdoc/>
    public IApplicationResourceDefinition AddResource(ProviderName provider, ResourceName name)
    {
        return _model.AddResource(provider, name);
    }

    /// <inheritdoc/>
    public ApplicationModelManifest GetManifest()
    {
        return _model.GetManifest();
    }

    /// <summary>
    /// Sets the parent gateway this one joins when it runs, and names it as the model's parent.
    /// </summary>
    /// <param name="parent">The parent, as this gateway reaches it.</param>
    /// <returns>The parent, for the outputs of its resources this gateway's settings use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The parent has this gateway's name.</exception>
    /// <exception cref="InvalidOperationException">The gateway already joins a parent, or is built.</exception>
    public IApplicationModelParent UseParent(IApplicationGatewayParent parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ThrowIfBuilt();

        IApplicationModelParent reference = _model.UseParent(parent.Name);
        _parent = parent;
        return reference;
    }

    /// <summary>
    /// Sets the infrastructure the gateway deploys onto, which is also the channel its instances
    /// reach it through. The gateway borrows it: the caller disposes it once the gateway has stopped.
    /// </summary>
    /// <remarks>
    /// It replaces a platform set by <see cref="UsePlatform{TPlatform}"/> as the infrastructure; the
    /// platform stays the instance client unless <see cref="UseInstanceClient"/> replaces it too.
    /// </remarks>
    /// <param name="infrastructure">The infrastructure provider.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="infrastructure"/> names no platform.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    public ApplicationGatewayBuilder UseInfrastructure(IApplicationGatewayInfrastructureProvider infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);

        if (infrastructure.Platform.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayPlatformMissing, nameof(infrastructure));
        }

        ThrowIfBuilt();
        _infrastructure = infrastructure;
        return this;
    }

    /// <summary>
    /// Sets how the gateway calls its instances' control planes: the instance client of the
    /// platform its instances run on. A gateway without one deploys and receives publications but
    /// cannot run an operator's action. The gateway borrows it: the caller disposes it once the gateway
    /// has stopped.
    /// </summary>
    /// <remarks>
    /// It replaces a platform set by <see cref="UsePlatform{TPlatform}"/> as the instance client; the
    /// platform stays the infrastructure unless <see cref="UseInfrastructure"/> replaces it too.
    /// </remarks>
    /// <param name="client">The client.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    public ApplicationGatewayBuilder UseInstanceClient(IApplicationGatewayInstanceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        ThrowIfBuilt();
        _client = client;
        return this;
    }

    /// <summary>
    /// Sets the platform the gateway runs on, which <paramref name="factory"/> creates when the gateway
    /// is built: it is both the infrastructure the gateway deploys onto and the instance client it
    /// calls its instances with. The gateway owns it and disposes it when the gateway is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Build"/> calls the factory once, after everything else it checks has passed, so a
    /// builder that is never built, or whose build fails first, creates nothing. When the build fails
    /// after the factory ran, the build disposes the platform before it throws.
    /// </para>
    /// <para>
    /// It replaces the infrastructure, the instance client, and the platform set earlier; a later
    /// <see cref="UseInfrastructure"/> or <see cref="UseInstanceClient"/> replaces the platform in
    /// that role only. A platform's registration verb, such as the in-process
    /// <c>UseInProcess(configure)</c>, calls it with the platform's builder.
    /// </para>
    /// </remarks>
    /// <typeparam name="TPlatform">The gateway half of the platform.</typeparam>
    /// <param name="factory">Creates the platform.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    public ApplicationGatewayBuilder UsePlatform<TPlatform>(Func<TPlatform> factory)
        where TPlatform : IApplicationGatewayInfrastructureProvider, IApplicationGatewayInstanceClient
    {
        ArgumentNullException.ThrowIfNull(factory);
        ThrowIfBuilt();
        _platform = () =>
        {
            TPlatform platform = factory();
            return (platform, platform);
        };
        _infrastructure = null;
        _client = null;
        return this;
    }

    /// <summary>
    /// Sets where the gateway keeps its record, so a restarted gateway reconciles against it; the
    /// record stays in memory unless set.
    /// </summary>
    /// <param name="store">The record store.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    public ApplicationGatewayBuilder UseStore(IApplicationGatewayStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ThrowIfBuilt();
        _store = store;
        return this;
    }

    /// <summary>
    /// Builds the model, unless it is built, and the gateway that runs it. The builder accepts no
    /// changes afterward.
    /// </summary>
    /// <remarks>
    /// A platform set by <see cref="UsePlatform{TPlatform}"/> is created last, once the model is built,
    /// and only when it still fills a role; the gateway owns it.
    /// </remarks>
    /// <returns>The gateway.</returns>
    /// <exception cref="ArgumentException">The model breaks a rule; the message names the entry.</exception>
    /// <exception cref="InvalidOperationException">
    /// No infrastructure provider or platform is set; the model names a parent the gateway has no
    /// connection to; the platform's factory returned <see langword="null"/>, or a platform that names
    /// no platform; or the gateway is already built.
    /// </exception>
    public ApplicationGateway Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayAlreadyBuilt);
        }

        if (_infrastructure is null && _platform is null)
        {
            throw new InvalidOperationException(Format(ErrorMessages.GatewayInfrastructureMissing, Name));
        }

        // A parent named only through the model builder has no connection to join it by.
        if (_model.Parent is { } parent && _parent is null)
        {
            throw new InvalidOperationException(Format(ErrorMessages.GatewayParentMissing, Name, parent.Name));
        }

        IApplicationModel model = _built ?? BuildModel();

        IApplicationGatewayInfrastructureProvider? infrastructure = _infrastructure;
        IApplicationGatewayInstanceClient? client = _client;
        IApplicationGatewayInfrastructureProvider? owned = null;
        if (_platform is not null && (infrastructure is null || client is null))
        {
            (IApplicationGatewayInfrastructureProvider Infrastructure, IApplicationGatewayInstanceClient Client) platform = _platform();
            if (platform.Infrastructure is null)
            {
                throw new InvalidOperationException(Format(ErrorMessages.GatewayPlatformNull, Name));
            }

            owned = platform.Infrastructure;
            if (infrastructure is null && platform.Infrastructure.Platform.Value is null)
            {
                // The gateway never received it, so the failed build is what disposes it.
                DisposeCreated(owned);
                throw new InvalidOperationException(Format(ErrorMessages.GatewayPlatformUnnamed, Name));
            }

            infrastructure ??= platform.Infrastructure;
            client ??= platform.Client;
        }

        _isBuilt = true;
        return new ApplicationGateway(
            model,
            infrastructure!,
            _store ?? new InMemoryApplicationGatewayStore(),
            _parent,
            client,
            owned,
            _startupTimeout,
            _leaseDuration);
    }

    IApplicationModelParent IApplicationModelBuilder.UseParent(GatewayName name)
    {
        ThrowIfBuilt();
        return _model.UseParent(name);
    }

    IApplicationModel IApplicationModelBuilder.Build()
    {
        return BuildModel();
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseInfrastructure(IApplicationGatewayInfrastructureProvider infrastructure)
    {
        return UseInfrastructure(infrastructure);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseInstanceClient(IApplicationGatewayInstanceClient client)
    {
        return UseInstanceClient(client);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UsePlatform<TPlatform>(Func<TPlatform> factory)
    {
        return UsePlatform(factory);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseStore(IApplicationGatewayStore store)
    {
        return UseStore(store);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseParent(IApplicationGatewayParent parent)
    {
        UseParent(parent);
        return this;
    }

    IApplicationGateway IApplicationGatewayBuilder.Build()
    {
        return Build();
    }

    // The model builder refuses a second build; the gateway reuses the first.
    private IApplicationModel BuildModel()
    {
        _built = _model.Build();
        return _built;
    }

    // Build is synchronous, so a platform that disposes only asynchronously is waited for here; this
    // runs only when a build fails.
    private static void DisposeCreated(object platform)
    {
        if (platform is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (platform is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void ThrowIfBuilt()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayAlreadyBuilt);
        }
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationGatewayBuilder _builder;

        public DebugView(ApplicationGatewayBuilder builder)
        {
            _builder = builder;
        }

        public GatewayName Name => _builder.Name;

        public IApplicationModelParent? Parent => _builder.Parent;

        public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _builder.Resources;

        public TimeSpan StartupTimeout => _builder.StartupTimeout;

        public TimeSpan LeaseDuration => _builder.LeaseDuration;
    }
}
