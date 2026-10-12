namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Reports optional transport limits for peer-initiated streams on a multiplexed connection.
/// </summary>
/// <remarks>
/// <para>
/// A transport registers an implementation in <see cref="IMultiplexedConnection.Features"/> with
/// <c>features.Set(feature)</c>, normally in <c>ConfigureFeatures</c>. The implementation's
/// <see cref="IConnectionFeature.Name"/> returns <see cref="FeatureName"/>. Consumers read the
/// <c>connection.StreamLimits</c> extension property, which returns <see langword="null"/> when no
/// stream-limit feature is visible. The connection object need not implement this interface itself.
/// The feature supplies transport facts without requiring a connection to interpret application protocols.
/// </para>
/// <para>
/// A wrapper deriving from <see cref="MultiplexedConnection"/> propagates the wrapped connection's
/// <c>Features</c> through <c>InnerFeatures</c>, without manually implementing and forwarding this
/// interface. Registering the same feature name replaces the inner feature; removing that name hides it.
/// </para>
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
