# Assimalign.Cohesion.Connections.StreamAbort

Abandon one stream direction with an application error code. The package owns `IMultiplexedStreamAbort` and the `DirectionalAbort` extension property on `IConnection` in the `Assimalign.Cohesion.Connections` namespace.

## Usage

Reference this package, import `Assimalign.Cohesion.Connections`, and read `connection.DirectionalAbort`. The property returns the registered feature or `null` when the connection does not offer it. Cache the result when using it repeatedly on a stream or connection.

Drivers register an implementation with `features.Set(feature)` in `ConfigureFeatures`. Its `Name` returns `IMultiplexedStreamAbort.FeatureName`, pinned to "StreamAbort". Layers return the wrapped connection's collection from `InnerFeatures` to pass the feature through, or replace or remove that named slot.

`AbortRead(code)` stops receiving and asks the peer to stop sending; `AbortWrite(code)` resets sending. Each direction keeps its first end signal. Neither call ends the local stream lifecycle; callers can subsequently use `Abort(reason)` to do that. The contract preserves its existing code range and failure semantics.

## Dependencies

The package references only `Assimalign.Cohesion.Connections`, which owns the feature collection and connection contracts. See [DESIGN.md](DESIGN.md) for registration, layering and compatibility.
