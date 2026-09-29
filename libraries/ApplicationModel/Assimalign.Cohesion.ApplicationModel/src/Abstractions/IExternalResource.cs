namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A resource whose desired state is imported through an <see cref="IExternalResourceResolver"/>.
/// </summary>
public interface IExternalResource : IApplicationResource
{
    /// <summary>Gets the build-produced application-boundary declaration.</summary>
    ExternalResourceDeclaration Declaration { get; }

    /// <summary>Gets the environment-selected resolver for the declaration.</summary>
    IExternalResourceResolver Resolver { get; }
}
