using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// An application-set member's resolved model with the provider registrations the set registered
/// for that member attached. Every other member forwards to the resolved model, so descriptors,
/// resources, manifests, plans, and commands keep their identity for the gateway.
/// </summary>
/// <remarks>
/// A model imported from a describe output or an export carries
/// <see cref="ApplicationProviders.Empty"/>; wrapping it rather than rebuilding it keeps the
/// resources the set binds externals on, and works for any <see cref="IApplicationModelResolver"/>.
/// </remarks>
internal sealed class ProviderBoundApplicationModel : IApplicationModel
{
    private readonly IApplicationModel _model;

    /// <summary>
    /// Attaches frozen registrations to a resolved member model.
    /// </summary>
    /// <param name="model">The member's resolved model.</param>
    /// <param name="providers">The member's frozen registrations.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="model"/> or <paramref name="providers"/> is <see langword="null"/>.
    /// </exception>
    public ProviderBoundApplicationModel(IApplicationModel model, ApplicationProviders providers)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(providers);
        Providers = providers.ToFrozen();
    }

    public ApplicationName Name => _model.Name;

    public IApplicationEnvironment Environment => _model.Environment;

    public GatewayRunMode RunMode => _model.RunMode;

    public ResourceName GatewayIdentity => _model.GatewayIdentity;

    public string Owner => _model.Owner;

    public bool Adopt => _model.Adopt;

    public bool RestartOrphans => _model.RestartOrphans;

    public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => _model.Descriptors;

    public IReadOnlyList<IApplicationResource> Resources => _model.Resources;

    public IReadOnlyList<ResourceManifest> Manifests => _model.Manifests;

    public IReadOnlyList<ResourcePlan> Plans => _model.Plans;

    public IReadOnlyList<IResourceCommand> Commands => _model.Commands;

    public ApplicationProviders Providers { get; }
}
