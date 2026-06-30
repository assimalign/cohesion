namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Describes the delivery guarantees and features of a transport, so that higher layers can
/// select a transport by capability rather than by protocol identity.
/// </summary>
/// <param name="Protocol">The underlying protocol identity. For diagnostics only; do not branch behavior on it.</param>
/// <param name="Delivery">Whether the transport delivers a byte stream or discrete datagrams.</param>
/// <param name="IsReliable">Whether delivery is guaranteed, with lost data retransmitted.</param>
/// <param name="IsOrdered">Whether data is delivered in the order it was sent.</param>
/// <param name="IsMultiplexed">Whether the transport carries multiple independent streams over one connection.</param>
/// <param name="Security">The transport-level security applied to the connection.</param>
public readonly record struct ConnectionCapabilities(
    ConnectionProtocol Protocol,
    ConnectionDelivery Delivery,
    bool IsReliable,
    bool IsOrdered,
    bool IsMultiplexed,
    ConnectionSecurity Security);
