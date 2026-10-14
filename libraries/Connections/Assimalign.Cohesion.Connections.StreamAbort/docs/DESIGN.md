# Assimalign.Cohesion.Connections.StreamAbort Design

## Design intent

Abandon one stream direction with an application error code. Owner decision 56 (#1452) places this optional capability in its own package, keeping the Connections core limited to shared contracts and generic feature discovery. Consumers read `DirectionalAbort` on `IMultiplexedConnectionStream` instead of testing a connection's runtime type.

## Family map

The feature package depends on core Connections; drivers and consumers reference the feature package.

```mermaid
flowchart LR
    Driver["Driver or consumer"] --> Feature["Connections.StreamAbort"]
    Feature --> Core["Connections"]
```

| Package | Role |
| --- | --- |
| `Assimalign.Cohesion.Connections` | Connection roots, guided bases and layered feature collection. |
| `Assimalign.Cohesion.Connections.StreamAbort` | `IMultiplexedStreamAbort` and `DirectionalAbort` discovery. |

## Registration and layering

The contract extends `IConnectionFeature`; every implementation returns `IMultiplexedStreamAbort.FeatureName` ("StreamAbort") as its `Name`. One feature object fills one named slot. A driver registers the object in `ConfigureFeatures`, which runs once when its collection is first accessed.

The accessor uses `Features.Get<IMultiplexedStreamAbort>()`. The connection's own features take precedence over inner features, and a removed or replaced name hides that inner slot. Typed lookup on the concrete collection allocates nothing. Layers opt into pass-through by overriding `InnerFeatures`; a layer that invalidates the capability removes its slot. A multiplexed stream does not inherit features from its parent connection.

## Semantics and lifetime

`AbortRead(code)` stops receiving and asks the peer to stop sending; `AbortWrite(code)` resets sending. Each direction keeps its first end signal. Neither call ends the local stream lifecycle; callers can subsequently use `Abort(reason)` to do that. The root `stream.Abort(code, reason)` resets both directions and ends the lifecycle in one call, retaining each direction's first code.

Directional abort remains optional after #1453: QUIC, WebTransport and the in-memory model can end one direction independently; HTTP/2, SSH, yamux and mplex cannot. Whole-stream reset and clean input completion belong to the root `IMultiplexedConnectionStream`.

The collection does not own or dispose feature objects. Features that delegate to a driver share the driver's lifetime. QUIC registers distinct objects for TLS and stream limits; its stream registers itself for directional abort, as does the in-memory multiplexed stream.

## Compatibility and AOT

The contract's existing members move unchanged from Connections. Its XML documentation describes feature registration, the `DirectionalAbort` accessor and `InnerFeatures` propagation. The contract did not ship in 10.0.0-preview.1, so its move introduces no released API break. Adding `Features` to the three connection root interfaces does break preview.1 implementers, accepted under decision 15; subclasses of the guided bases inherit the implementation.

Narrowing `DirectionalAbort` from `IConnection` to `IMultiplexedConnectionStream` in #1453 affects unreleased callers only. Multiplexed streams returned by accept/open already have the new root type.

The package uses ordinary interfaces and a C# extension property, with no reflection, dynamic generation or generic virtual lookup on the read path. NativeAOT compatibility remains enabled by repository defaults.
