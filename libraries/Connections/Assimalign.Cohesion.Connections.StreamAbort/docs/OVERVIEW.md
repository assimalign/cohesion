# Assimalign.Cohesion.Connections.StreamAbort

Abandon one stream direction with an application error code. The package owns `IMultiplexedStreamAbort` and the `DirectionalAbort` extension property on `IMultiplexedConnectionStream` in the `Assimalign.Cohesion.Connections` namespace.

## Usage

Reference this package, import `Assimalign.Cohesion.Connections`, and read `stream.DirectionalAbort`. The property returns the registered feature or `null` when the stream does not offer it. Cache the result when using it repeatedly on a stream.

Drivers register an implementation with `features.Set(feature)` in `ConfigureFeatures`. Its `Name` returns `IMultiplexedStreamAbort.FeatureName`, pinned to "StreamAbort". Layers return the wrapped connection's collection from `InnerFeatures` to pass the feature through, or replace or remove that named slot.

`AbortRead(code)` stops receiving and asks the peer to stop sending; `AbortWrite(code)` resets sending. Each direction keeps its first end signal. Neither call ends the local stream lifecycle; callers can subsequently use `Abort(reason)` to do that. The root `stream.Abort(code, reason)` resets both directions and ends the lifecycle in one call, retaining each direction's first code.

## Dependencies

The package references only `Assimalign.Cohesion.Connections`, which owns the feature collection and connection contracts. See [DESIGN.md](DESIGN.md) for registration, layering and compatibility.
