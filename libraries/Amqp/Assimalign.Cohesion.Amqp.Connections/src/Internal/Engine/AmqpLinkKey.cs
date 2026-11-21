using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Identifies a link between two containers: its name is unique among the links of its direction between them,
/// so the link is active on one connection at a time (AMQP 1.0 Part 2, section 2.6.1).
/// </summary>
/// <param name="RemoteContainerId">The container identifier the peer sent in its <c>open</c>.</param>
/// <param name="ContainerId">The container identifier this endpoint sent in its <c>open</c>.</param>
/// <param name="Name">The link's name.</param>
/// <param name="Role">The local role, which with the two containers gives the link's direction.</param>
internal readonly record struct AmqpLinkKey(string RemoteContainerId, string ContainerId, string Name, AmqpRole Role);
