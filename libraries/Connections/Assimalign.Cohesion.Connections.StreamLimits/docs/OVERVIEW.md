# Assimalign.Cohesion.Connections.StreamLimits

Read the cumulative inbound bidirectional stream allowance of a multiplexed transport. The package owns `IMultiplexedConnectionStreamLimits` and the `StreamLimits` extension property on `IMultiplexedConnection` in the `Assimalign.Cohesion.Connections` namespace.

## Usage

Reference this package, import `Assimalign.Cohesion.Connections`, and read `connection.StreamLimits`. The property returns the registered feature or `null` when the connection does not offer it. Cache the result when using it repeatedly on a stream or connection.

Drivers register an implementation with `features.Set(feature)` in `ConfigureFeatures`. Its `Name` returns `IMultiplexedConnectionStreamLimits.FeatureName`, pinned to "StreamLimits". Layers return the wrapped connection's collection from `InnerFeatures` to pass the feature through, or replace or remove that named slot.

`InboundBidirectionalStreamLimit` is a nonnegative, nondecreasing conservative upper bound on the number of bidirectional streams the peer may have been allowed to initiate over the connection lifetime. It includes credit whose release has begun but may not yet have been advertised. It is not a concurrent-stream count or permission to open an outbound stream. The multiplexed root migration (#1453) retains this optional feature: QUIC and WebTransport have cumulative allowances; HTTP/2's concurrent-stream limit is a different quantity.

## Dependencies

The package references only `Assimalign.Cohesion.Connections`, which owns the feature collection and connection contracts. See [DESIGN.md](DESIGN.md) for registration, layering and compatibility.
