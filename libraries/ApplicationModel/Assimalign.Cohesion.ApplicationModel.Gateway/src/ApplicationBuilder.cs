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
/// </remarks>
[DebuggerDisplay("{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ApplicationBuilder : IApplicationModelBuilder, IApplicationGatewayBuilder
{
    private static readonly TimeSpan _defaultStartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _defaultLeaseDuration = TimeSpan.FromSeconds(30);

    private readonly IApplicationModelBuilder _model;
    private readonly List<IApplicationGatewayTransport> _transports = [];
    private IInfrastructureProvider? _infrastructure;
    private IApplicationGatewayStore? _store;
    private IApplicationInstanceClient? _client;
    private IApplicationParentGateway? _parent;
    private IApplicationModel? _built;
    private TimeSpan _startupTimeout = _defaultStartupTimeout;
    private TimeSpan _leaseDuration = _defaultLeaseDuration;
    private bool _isBuilt;

    internal ApplicationBuilder(GatewayName name)
    {
        _model = ApplicationModel.CreateBuilder(name);
    }

    /// <inheritdoc/>
    public GatewayName Name => _model.Name;

    /// <inheritdoc/>
    public IApplicationParent? Parent => _model.Parent;

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
    public ApplicationManifest GetManifest()
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
    public IApplicationParent UseParent(IApplicationParentGateway parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ThrowIfBuilt();

        IApplicationParent reference = _model.UseParent(parent.Name);
        _parent = parent;
        return reference;
    }

    /// <summary>
    /// Sets the infrastructure the gateway deploys onto.
    /// </summary>
    /// <param name="infrastructure">The infrastructure provider.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="infrastructure"/> names no platform.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    public ApplicationBuilder UseInfrastructure(IInfrastructureProvider infrastructure)
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
    /// Adds a transport the gateway's instances reach it through. A gateway may have several, and
    /// it starts each when it runs.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The transport is already added.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    public ApplicationBuilder UseTransport(IApplicationGatewayTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ThrowIfBuilt();

        if (_transports.Contains(transport))
        {
            throw new ArgumentException(ErrorMessages.GatewayTransportDuplicate, nameof(transport));
        }

        _transports.Add(transport);
        return this;
    }

    /// <summary>
    /// Sets how the gateway calls its instances' control planes: the instance client of the
    /// platform its instances run on. A gateway without one deploys and receives publications but
    /// cannot run an operator's action.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The gateway is built.</exception>
    public ApplicationBuilder UseInstanceClient(IApplicationInstanceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        ThrowIfBuilt();
        _client = client;
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
    public ApplicationBuilder UseStore(IApplicationGatewayStore store)
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
    /// <returns>The gateway.</returns>
    /// <exception cref="ArgumentException">The model breaks a rule; the message names the entry.</exception>
    /// <exception cref="InvalidOperationException">
    /// No infrastructure provider is set; the model names a parent the gateway has no connection to;
    /// or the gateway is already built.
    /// </exception>
    public ApplicationGateway Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayAlreadyBuilt);
        }

        if (_infrastructure is null)
        {
            throw new InvalidOperationException(Format(ErrorMessages.GatewayInfrastructureMissing, Name));
        }

        // A parent named only through the model builder has no connection to join it by.
        if (_model.Parent is { } parent && _parent is null)
        {
            throw new InvalidOperationException(Format(ErrorMessages.GatewayParentMissing, Name, parent.Name));
        }

        IApplicationModel model = _built ?? BuildModel();
        _isBuilt = true;
        return new ApplicationGateway(
            model,
            _infrastructure,
            _store ?? new InMemoryApplicationGatewayStore(),
            _parent,
            [.. _transports],
            _client,
            _startupTimeout,
            _leaseDuration);
    }

    IApplicationParent IApplicationModelBuilder.UseParent(GatewayName name)
    {
        ThrowIfBuilt();
        return _model.UseParent(name);
    }

    IApplicationModel IApplicationModelBuilder.Build()
    {
        return BuildModel();
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseInfrastructure(IInfrastructureProvider infrastructure)
    {
        return UseInfrastructure(infrastructure);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseTransport(IApplicationGatewayTransport transport)
    {
        return UseTransport(transport);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseInstanceClient(IApplicationInstanceClient client)
    {
        return UseInstanceClient(client);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseStore(IApplicationGatewayStore store)
    {
        return UseStore(store);
    }

    IApplicationGatewayBuilder IApplicationGatewayBuilder.UseParent(IApplicationParentGateway parent)
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
        private readonly ApplicationBuilder _builder;

        public DebugView(ApplicationBuilder builder)
        {
            _builder = builder;
        }

        public GatewayName Name => _builder.Name;

        public IApplicationParent? Parent => _builder.Parent;

        public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _builder.Resources;

        public TimeSpan StartupTimeout => _builder.StartupTimeout;

        public TimeSpan LeaseDuration => _builder.LeaseDuration;
    }
}
