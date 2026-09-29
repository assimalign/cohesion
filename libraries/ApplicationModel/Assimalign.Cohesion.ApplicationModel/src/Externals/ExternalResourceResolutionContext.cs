using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Supplies an external declaration and optional peer-control-plane client to a resolver.
/// </summary>
public sealed class ExternalResourceResolutionContext
{
    /// <summary>Initializes an external resolution context.</summary>
    /// <param name="declaration">The external resource to resolve.</param>
    /// <param name="controlPlaneClient">The optional transport for peer gateway resolution.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="declaration"/> is <see langword="null"/>.
    /// </exception>
    public ExternalResourceResolutionContext(
        ExternalResourceDeclaration declaration,
        IControlPlaneClient? controlPlaneClient = null)
    {
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        ControlPlaneClient = controlPlaneClient;
    }

    /// <summary>Gets the external declaration being resolved.</summary>
    public ExternalResourceDeclaration Declaration { get; }

    /// <summary>Gets the configured peer-gateway client, when one is available.</summary>
    public IControlPlaneClient? ControlPlaneClient { get; }
}
