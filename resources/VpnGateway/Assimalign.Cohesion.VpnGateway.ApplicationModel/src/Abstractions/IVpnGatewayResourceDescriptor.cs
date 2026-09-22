using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a VpnGateway resource together with its application-graph dependencies.
/// </summary>
public interface IVpnGatewayResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed VpnGateway resource wrapped by this descriptor.
    /// </summary>
    new VpnGatewayResource Resource { get; }
}
