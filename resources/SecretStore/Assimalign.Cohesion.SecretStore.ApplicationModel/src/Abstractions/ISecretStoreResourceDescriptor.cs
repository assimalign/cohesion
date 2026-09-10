using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel;

/// <summary>
/// Describes a secret-store resource together with its application-graph dependencies.
/// </summary>
public interface ISecretStoreResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed secret-store resource wrapped by this descriptor.
    /// </summary>
    new SecretStoreResource Resource { get; }
}
