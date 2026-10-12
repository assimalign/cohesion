namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Reports optional transport limits for peer-initiated streams on a multiplexed connection.
/// </summary>
/// <remarks>
/// Consumers discover this facet with a type test on <see cref="IMultiplexedConnection"/>.
/// It supplies transport facts without requiring a connection to interpret application protocols.
/// </remarks>
public interface IMultiplexedConnectionStreamLimits : IConnectionFeature
{
    /// <summary>
    /// Gets the name of the connection feature.
    /// </summary>
    public const string FeatureName = "StreamLimits";

    /// <summary>
    /// Gets a conservative upper bound on the cumulative number of bidirectional streams the peer
    /// may have been permitted to initiate during this connection's lifetime.
    /// </summary>
    /// <remarks>
    /// This nonnegative, nondecreasing value is a cumulative allowance, not a count of currently open
    /// streams. A transport may include credit whose release has started but is not yet advertised;
    /// the value must never understate the permitted allowance. It is suitable for rejecting stream
    /// ordinals at or beyond the bound, not for deciding whether an outbound stream can be opened.
    /// </remarks>
    long InboundBidirectionalStreamLimit { get; }
}
