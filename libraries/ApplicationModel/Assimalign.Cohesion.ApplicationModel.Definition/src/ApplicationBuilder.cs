using System;
using System.Collections.Generic;
using System.Diagnostics;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Composes an application. The builder is the root application set: what the root gateway
/// deploys, with each child gateway's set below it.
/// </summary>
/// <remarks>
/// <see cref="Application.CreateBuilder(GatewayName, string[])"/> creates a builder. Composition
/// runs on one thread; the builder is not safe for concurrent changes.
/// </remarks>
[DebuggerDisplay("{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ApplicationBuilder : IApplicationBuilder
{
    private readonly ApplicationSetComposition _composition;
    private IApplicationGateway? _gateway;

    internal ApplicationBuilder(GatewayName name, string[] args)
    {
        Name = name;
        Args = Array.AsReadOnly((string[])args.Clone());
        _composition = new ApplicationSetComposition(this, new ApplicationHierarchy(name));
    }

    /// <inheritdoc/>
    public GatewayName Name { get; }

    /// <summary>
    /// Gets <see langword="null"/>: the builder is the root set.
    /// </summary>
    public IApplicationSet? Parent => null;

    /// <inheritdoc/>
    public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _composition.Resources;

    /// <inheritdoc/>
    public IReadOnlyCollection<IApplicationSet> Gateways => _composition.Gateways;

    /// <inheritdoc/>
    public IReadOnlyList<string> Args { get; }

    /// <inheritdoc/>
    public IApplicationResourceDefinition AddResource(ProviderName provider, ResourceName name)
    {
        return _composition.AddResource(provider, name);
    }

    /// <inheritdoc/>
    public IApplicationSet AddGateway(GatewayName name)
    {
        return _composition.AddGateway(name);
    }

    /// <inheritdoc/>
    public ApplicationManifest GetManifest()
    {
        return _composition.GetManifest();
    }

    /// <summary>
    /// Selects the gateway that runs the application.
    /// </summary>
    /// <param name="gateway">The gateway.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gateway"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The builder already has a gateway, or has already built its application.
    /// </exception>
    public ApplicationBuilder UseGateway(IApplicationGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        _composition.Hierarchy.ThrowIfBuilt();

        if (_gateway is not null)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayAlreadySet);
        }

        _gateway = gateway;
        return this;
    }

    /// <summary>
    /// Closes composition and builds the application.
    /// </summary>
    /// <remarks>
    /// The model is created from the builder's manifest by <see cref="ApplicationModel.Create(ApplicationManifest)"/>,
    /// so a model built in code passes the same checks as one a gateway receives as data. No gateway
    /// is needed to build; one is needed to run.
    /// </remarks>
    /// <returns>The application.</returns>
    /// <exception cref="InvalidOperationException">The builder has already built its application.</exception>
    public Application Build()
    {
        if (_composition.Hierarchy.IsBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationAlreadyBuilt);
        }

        _composition.Hierarchy.MarkBuilt();

        return new Application(ApplicationModel.Create(GetManifest()), _gateway);
    }

    IApplicationBuilder IApplicationBuilder.UseGateway(IApplicationGateway gateway)
    {
        return UseGateway(gateway);
    }

    IApplication IApplicationBuilder.Build()
    {
        return Build();
    }

    private sealed class DebugView
    {
        private readonly ApplicationBuilder _builder;

        public DebugView(ApplicationBuilder builder)
        {
            _builder = builder;
        }

        public GatewayName Name => _builder.Name;

        public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _builder.Resources;

        public IReadOnlyCollection<IApplicationSet> Gateways => _builder.Gateways;

        public IReadOnlyList<string> Args => _builder.Args;
    }
}
