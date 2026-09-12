using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.NatGateway.ApplicationModel;

/// <summary>
/// Describes a NatGateway resource together with its application-graph dependencies.
/// </summary>
public interface INatGatewayResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed NatGateway resource wrapped by this descriptor.
    /// </summary>
    new NatGatewayResource Resource { get; }
}
