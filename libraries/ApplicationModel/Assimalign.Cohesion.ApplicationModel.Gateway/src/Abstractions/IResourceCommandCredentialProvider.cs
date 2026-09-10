namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Supplies the current bootstrap credential used to dispatch a command to one realized resource.
/// </summary>
public interface IResourceCommandCredentialProvider
{
    /// <summary>Gets the resource-scoped bearer credential for command dispatch.</summary>
    /// <param name="application">The application that owns the realized resource.</param>
    /// <param name="resource">The target resource.</param>
    /// <returns>The current ES256 bootstrap credential.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// The application is not active in the gateway session.
    /// </exception>
    string GetResourceCommandCredential(ApplicationName application, ResourceName resource);
}
