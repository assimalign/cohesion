using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// The <see cref="IApplicationProviderBuilder"/> an application set hands a member's registration
/// callback: a fresh, mutable <see cref="ApplicationProviders"/> for that member alone, and a lookup
/// over the member's resolved model.
/// </summary>
/// <remarks>
/// <see cref="Complete"/> freezes the registrations when the callback returns. From then on
/// <see cref="Providers"/> is the frozen snapshot, so a registration made through a surface the
/// callback kept a reference to throws instead of silently missing the member's model.
/// </remarks>
internal sealed class ApplicationSetMemberProviderBuilder : IApplicationProviderBuilder
{
    private readonly IApplicationModel _model;
    private ApplicationProviders _providers = new();

    /// <summary>
    /// Initializes the registration surface of one resolved member model.
    /// </summary>
    /// <param name="model">The member's resolved model.</param>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    public ApplicationSetMemberProviderBuilder(IApplicationModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public ApplicationName? Application => _model.Name;

    public ApplicationProviders Providers => _providers;

    public bool TryGetResourceManifest(ResourceName resource, [NotNullWhen(true)] out ResourceManifest? manifest)
    {
        IReadOnlyList<ResourceManifest> manifests = _model.Manifests;
        for (int index = 0; index < manifests.Count; index++)
        {
            if (manifests[index].Name == resource)
            {
                manifest = manifests[index];
                return true;
            }
        }

        manifest = null;
        return false;
    }

    /// <summary>
    /// Freezes the registrations and returns the snapshot to attach to the member's model.
    /// </summary>
    /// <returns>The frozen registrations.</returns>
    public ApplicationProviders Complete()
    {
        _providers = _providers.ToFrozen();
        return _providers;
    }
}
