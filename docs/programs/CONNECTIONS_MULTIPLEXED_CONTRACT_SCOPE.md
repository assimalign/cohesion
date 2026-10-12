# Connection Features and Multiplexed Contracts: Scope

**Date:** 2026-10-12. **Status:** approved by the owner on 2026-10-12 (`HTTP_WEB_PROGRAM_PLAN.md` §7.4, decision 56).
**Area:** Connections (epic #324). **Program:** HTTP/Web (`docs/programs/HTTP_WEB_PROGRAM_PLAN.md`), inserted into
Stage 14 ahead of #1300.

**Tracking, in order:**
1. #1452 (`[L01.01.14.18]`): the connection feature collection and three feature packages (Part 1).
2. #1453 (`[L01.01.14.19]`): the multiplexed roots (Part 2), with #1451 (`[L01.01.14.17]`) folded in.
3. #1300, which instruments the code both parts change.

Claims that are not plain from the code are tagged **[Certain]** (checked against the code at `abe3751cf`, or
against the specification cited), **[Likely]** (a strong inference) or **[Guessing]** (a gap filled by judgment).

## 1. The owner's asks (2026-10-12)

1. Roll `IMultiplexedStreamAbort` and the former coded-connection facet into the multiplexed root contract.
2. Explain the former input-completion facet, and consolidate it if possible.
3. Replace `IMultiplexedConnectionStreamLimits` with a generic type on the root.
4. Make sure none of it is shaped only for HTTP and QUIC. Other multiplexed transports must be able to implement
   the roots without dead members or conflicting semantics.
5. Replace the optional capabilities' type tests with a per-connection feature collection, like
   `IHttpFeatureCollection`. Each capability is its own flat package (`Assimalign.Cohesion.Connections.StreamLimits`,
   not `.Features.`), referenced explicitly by whoever offers or reads it. Build the features first, then the
   multiplexed roots.

## 2. Findings first

- **[Certain] Ask 4 overturned two of the first recommendations.** The first draft judged each member against
  the two drivers in this repository, and both carry codes per direction. §3 shows that is a QUIC-family
  property:
  - **Per-direction aborts:** HTTP/2, SSH, yamux and mplex cannot abort one direction alone, and SSH, yamux and
    mplex carry no stream code.
  - **The cumulative stream allowance:** only QUIC and WebTransport have one.
  
  Both stay optional capabilities, which Part 1 turns into features.
- **What every byte-stream multiplexer does have:** a half-close, a whole-stream reset and a connection close,
  most with a code. Those go on the roots (Part 2).
- **[Certain] Facets fail silently through decorators.** A decorator hides a facet unless it re-implements and
  forwards it by hand.
  - Five test stream decorators forward by hand. The DESIGN records the same cost for TLS: a layer above TLS
    "hides the facet unless it implements the facet too and forwards it".
  - The planned proxy-protocol, accounting, throttling and compression layers would each have to forward every
    capability beneath them.
  - A feature collection that shows the wrapped connection's features through removes that (Part 1).
- **[Certain] `ConnectionCapabilities` is not a candidate for the collection.** Every read happens on a listener
  or factory, before any connection exists:
  - HTTP's `ValidateStreamCapabilities` and its `isSecure` decision;
  - AMQP's `ThrowIfNotAmqpCarrier`;
  - `IConnectionLayer.Describe`.
  
  Every field in it is universal, so it stays a typed value.
- **[Certain] None of the four facets shipped in 10.0.0-preview.1.**

  | Facet | Added | Issue |
  |---|---|---|
  | `IMultiplexedStreamAbort`, the former coded-connection facet | `83dd297e`, 2026-10-09 | #1080 |
  | the former input-completion facet | `5a2ce987`, 2026-10-12 | #1427 |
  | `IMultiplexedConnectionStreamLimits` | `3c0e5c6e`, 2026-10-12 | #1425 |

  Neither did `ITlsConnectionInfo` (`d8e7b9a6`, 2026-10-06). Moving, renaming or deleting them breaks nothing
  released. Preview.1 did ship `IConnection`, `IMultiplexedConnection` and `IDatagramConnection`, so new members
  on them break preview.1 implementers (§10).
- **[Certain] The existing root already has one QUIC-family member.** `OpenStreamAsync(ConnectionDirection.WriteOnly)`
  opens a unidirectional stream, which HTTP/2, SSH, yamux and mplex do not have. (G) settles what such a
  transport does.

## 3. Transport survey

The roots must fit every byte-stream multiplexer, not just the two in this repository.

| | QUIC (RFC 9000) | WebTransport (H3 and H2 drafts) | HTTP/2 streams (RFC 9113) | SSH channels (RFC 4254) | yamux | mplex |
|---|---|---|---|---|---|---|
| Half-close, so input completion is observable | FIN | FIN | `END_STREAM` | `CHANNEL_EOF` | FIN | Close |
| Whole-stream reset | `RESET_STREAM` + `STOP_SENDING` | the same | `RST_STREAM` | `CHANNEL_CLOSE` | RST | Reset |
| Code on a stream reset | 62-bit | 32-bit | 32-bit | none | none | none |
| Abort one direction, other unaffected | yes | yes (`WT_RESET_STREAM`, `WT_STOP_SENDING` over H2) | no: `RST_STREAM` closes both | no | no | no |
| Code on the connection close | 62-bit | 32-bit plus a message (`WT_CLOSE_SESSION`) | 32-bit (`GOAWAY`) | 32-bit plus a description (`DISCONNECT`) | three fixed values (`GoAway`) | none |
| Cumulative inbound stream allowance | `MAX_STREAMS` | `WT_MAX_STREAMS` | none; `MAX_CONCURRENT_STREAMS` counts concurrent streams | none | none | none |
| Unidirectional streams | yes | yes | no | no | no | no |

**Sources [Certain]:**
- RFC 9113 §5, §5.1, §6.4, §6.5.2 and §6.8.
- RFC 4254 §5.1 and §5.3; RFC 4253 §11.1.
- The yamux specification (`hashicorp/yamux` `spec.md`): flags, `GoAway` codes 0x0 to 0x2, no stream limit.
- The mplex specification (`libp2p/specs` `mplex`): Close is a half-close, and Reset carries no code ("No way of
  propagating errors").
- draft-ietf-webtrans-http3 §4.4, §5.3 and §6, and draft-ietf-webtrans-http2 §6.2, §6.3, §6.7 and §6.12.

SCTP (RFC 9260) multiplexes message-oriented streams with no per-stream close. It fits the datagram shape, not
`IMultiplexedConnection`'s duplex pipes, so it does not constrain this contract.

**The rule.**
- A member goes on a root only when every transport in the table can honor its documented semantics.
- A capability only some transports have is a feature: the connection registers it, and a consumer reads it from
  `Features`.
- A member that most transports would implement as a no-op, or by breaking its own documentation, is the dead
  member this change must not create.

## 4. What the former input-completion facet does

It tells HTTP/3 that the peer's FIN has already arrived, without another read (#1427).

- **[Certain] The gap.** QUIC's input is `PipeReader.Create(QuicStream)`, which learns of a FIN only when a read
  returns 0. A GET whose FIN arrived with its HEADERS still looked as if a body could follow, so almost every
  HTTP/3 request reported `CanHaveBody = true`.
- **The cost.** Each such request got the client-fault gate's response sink, control, context and feature (240 B
  per bodiless request, measured in Web.Hosting's `Http3InputCompletionAllocationTests`), and automatic
  `100 Continue` was armed.
- **The mechanism.** QUIC answers `!_readAborted && ReadsClosed.IsCompletedSuccessfully`.
  `Http3RequestStreamReader.IsEndBuffered` samples it before probing the pipe, and trusts it only when the pipe
  buffer is empty.
- **Where it goes.** Every transport in §3 has a half-close, so Part 2 makes it a member of the root stream
  contract.

## 5. Inventory at `abe3751cf`

The four facets appear in 42 files (135 occurrences, docs included), and `ITlsConnectionInfo` in its own set.

**Multiplexed connections (13).**
- Source: `QuicMultiplexedConnection` (public) and `InMemoryMultiplexedConnection` (internal).
- Test doubles:
  - Connections tests: `TestMultiplexedConnection`.
  - Amqp tests: `TestMultiplexedConnection`.
  - Http.Connections tests: `TestMultiplexedConnection`, `TestTlsMultiplexedConnection`,
    `CodeRecordingMultiplexedConnection`, `CompletionTrackingMultiplexedConnection`,
    `StallingMultiplexedConnection`, `StreamInputMultiplexedConnection`, and `Http3PriorityTests`'
    `LimitedMultiplexedConnection`.
  - Web.Hosting tests: `CompletedInputMultiplexedConnection` and `ResponseMultiplexedConnection`.

**Streams.**
- Source: `QuicStreamConnection` and the in-memory `StreamEnd`, which inherits `InMemoryConnection`.
- Test doubles: Http.Connections' `TestConnection`, which `TestMultiplexedConnection` queues as streams and the
  HTTP/1.1 and HTTP/2 tests also use, plus `StallingConnection`, `StreamInputConnection`, `TrackedConnection`, and
  Web.Hosting's `CompletedInputConnection` and `ResponseConnection`.

**`ITlsConnectionInfo`.**
- Implemented by `TlsConnection` (Connections.Security), `QuicMultiplexedConnection` and HTTP's
  `HttpTlsConnectionInfo`.
- Read by `Http3ConnectionContext.cs:150`, `HttpAlpnConnectionFactory.cs:65`, `HttpStreamConnectionContext.cs:21`
  and Http.Tls' `HttpTlsConnectionExtensions.cs:55`. The last reads HTTP's connection-info object, not a
  connection.

**Consumers of the multiplexed facets (all in HTTP/3).** All of these are in `libraries/Http/Assimalign.Cohesion.Http.Connections/src/Internal/Http3/`.

| Site | Today | After Part 1 | After Part 2 |
|---|---|---|---|
| `Http3ConnectionContext.cs:962` | type-test for the former coded-connection facet | unchanged | root `Abort(code, reason)` |
| `Http3ConnectionContext.cs:1006` (`ResetStream`) | `is IMultiplexedStreamAbort`, then `Abort(reason)` | `DirectionalAbort` | root stream `Abort(code, reason)` |
| `Http3ConnectionContext.cs:1024` (`StopReadingWithCode`) | `is IMultiplexedStreamAbort` | `DirectionalAbort` | `DirectionalAbort` |
| `Http3ConnectionContext.cs:1516` | `is IMultiplexedConnectionStreamLimits` | `StreamLimits` | `StreamLimits` |
| `Http3ConnectionContext.cs:2062`, `:542`, `:561` | streams typed `IConnection` | unchanged | `IMultiplexedStream` |
| `Http3ConnectionContext.cs:2153`, `Http3RequestStreamReader.cs:48` | cast to the former input-completion facet | unchanged | root `IsInputCompleted` |
| `Http3RequestBodyStream.cs:528`, `:540`, `:1038` | no-code fallback keyed on the facet | keyed on `DirectionalAbort` | keyed on `DirectionalAbort` |

**[Likely]** Amqp's `AmqpMultiplexedTransportConnection` holds streams as `IConnection` and compiles unchanged
through both parts.

**Tests that run over the in-memory multiplexed driver outside Connections:** Http.Connections, Web.Hosting,
Web.WebSockets and Web.Api.

## 6. Part 1: connection features (#1452)

### 6.1 The core, in `Assimalign.Cohesion.Connections`

```csharp
public interface IConnectionFeature
{
    string Name { get; }
}

public interface IConnectionFeatureCollection : IEnumerable<IConnectionFeature>
{
    IConnectionFeature? Get(string name);
    void Set<TFeature>(TFeature feature) where TFeature : class, IConnectionFeature; // replaces any feature with the same Name
    bool Remove(string name);                                                        // on a layer, also hides the wrapped connection's feature
}

public interface IConnection : IDuplexPipe, IAsyncDisposable
{
    // ...existing members...
    IConnectionFeatureCollection Features { get; }
}
// IMultiplexedConnection and IDatagramConnection carry the same property.

public class ConnectionFeatureCollection : IConnectionFeatureCollection
{
    // The wrapped connection's features show through unless replaced or removed.
    public ConnectionFeatureCollection(IConnectionFeatureCollection? inner = null);
}

public abstract class Connection : IConnection // the base does not implement the collection
{
    public IConnectionFeatureCollection Features { get; }                              // created on first access
    protected virtual IConnectionFeatureCollection? InnerFeatures => null;             // a layer returns its wrapped connection's
    protected virtual void ConfigureFeatures(IConnectionFeatureCollection features) { } // a driver registers its own here
}
// MultiplexedConnection and DatagramConnection get the same three members.

extension(IConnectionFeatureCollection features)
{
    // The connection's own features first, then the wrapped connection's.
    public TFeature? Get<TFeature>() where TFeature : class, IConnectionFeature;
}
```

### 6.2 The feature packages

| Package (flat; namespace `Assimalign.Cohesion.Connections`) | Contract and `FeatureName` | Extension property |
|---|---|---|
| `Assimalign.Cohesion.Connections.Tls` | `ITlsConnectionInfo`, `"Tls"` (members unchanged) | `Tls` on `IConnection` and `IMultiplexedConnection` |
| `Assimalign.Cohesion.Connections.StreamAbort` | `IMultiplexedStreamAbort`, `"StreamAbort"` (`AbortRead`, `AbortWrite`) | `DirectionalAbort` on `IConnection`; Part 2 narrows it to `IMultiplexedStream` |
| `Assimalign.Cohesion.Connections.StreamLimits` | `IMultiplexedConnectionStreamLimits`, `"StreamLimits"` (`InboundBidirectionalStreamLimit`) | `StreamLimits` on `IMultiplexedConnection` |

- **Names.** Each contract declares its `FeatureName` constant, and every implementation returns it. A layer's
  implementation therefore replaces the one below it.
- **One object, one slot.** Because `Name` is a single value, an object fills one slot.
  - **[Certain]** `QuicMultiplexedConnection`, which offers both TLS and stream limits, registers two small
    objects.
  - A stream offering one feature may register itself.
- **Namespace.** The packages declare `Assimalign.Cohesion.Connections`, the family-namespace precedent of
  `Http.Cookies` → `Http` in `general-rules.md`. The extension properties appear under the existing `using` as soon
  as the package is referenced.
- **The name `Connections.Tls`** holds the TLS facts, as `Http.Tls` does for HTTP. `Connections.Security` remains
  the TLS layer.
- **References:**

  | Package | References |
  |---|---|
  | Connections.Security | Connections.Tls |
  | Connections.Quic | all three |
  | Connections.InMemory | Connections.StreamAbort |
  | Http.Connections | all three |
  | Http.Tls | Connections.Tls |

- **Framework membership:**
  - The three join `Assimalign.Cohesion.Connections` as App kernel roots in `libraries/App/Assimalign.Cohesion.App.props`.
    That is 3 lines, against 54 if listed in each of the 18 area frameworks.
  - **[Certain]** Connections.Quic, Connections.Security and Http.Connections are private members of every area
    framework.
  - The framework closure tests confirm the placement.

### 6.3 Rules

- **Lookup.** The connection's own features come first. A removed name returns `null` and stops the lookup.
  After that come the wrapped connection's features. `Get(name)` matches names exactly. `Get<TFeature>()`
  returns the first feature that is a `TFeature`.
- **Streams keep their own features.** A multiplexed stream does not fall back to its connection's features, so
  connection-level facts never show up on a stream.
- **Byte-rewriting layers hide what they break.** A layer that rewrites the bytes removes any inner feature that
  acts on them. None exists on single-stream connections today. `Tls` describes the handshake, so it passes up
  through compression or accounting layers above TLS.
- **Public `Set`.** Consumers may attach typed features. This reverses the Connections DESIGN's non-goal "No
  general-purpose `Items` property bag on the connection". The owner decided it (decision 56), and the DESIGN
  records the reversal.
- **Allocation.** The collection is a separate object, created on first access. A QUIC stream pays only when
  HTTP/3 reads its features, so a request without a body allocates what it does today. The 240 B
  input-completion allocation test is the gate.
- **AOT.**
  - `Set<TFeature>` is a generic virtual method. **[Likely]** That is fine for a call made once at setup under
    NativeAOT.
  - The typed `Get<TFeature>()` is an extension member, as HTTP's is: no generic virtual dispatch on the hot path.
  - Hot-path consumers cache a feature once per connection or stream.
- **Part 2's interfaces wait.** The former coded-connection facet and the former input-completion facet stay as they are
  until Part 2 moves them onto the roots.

## 7. Part 2: the multiplexed roots (#1453)

Signed off by the owner (decision 56).

### (A) The connection close with a code moves to the root

`IMultiplexedConnection.Abort(long errorCode, Exception? reason = null)`, abstract on `MultiplexedConnection`.
It replaces the former coded-connection facet.
- Five of the six transports in §3 carry a close code.
- **The code space is the transport's.** QUIC takes 0 to 2^62 - 1; WebTransport, HTTP/2 and SSH take 32-bit
  codes. A driver throws `ArgumentOutOfRangeException` for a code it cannot carry, and a negative code is never
  valid.
- A transport whose close carries no code (mplex) closes as `Abort(reason)` does.
- The first abort or disposal decides the close.

### (B) A root stream contract with a whole-stream reset

`IMultiplexedStream : IConnection`, with the guided base `MultiplexedStream : Connection, IMultiplexedStream`.
`AcceptStreamAsync` and `OpenStreamAsync` return it, and `MultiplexedConnection`'s typed members return
`ValueTask<MultiplexedStream>`, as `ConnectionListener.AcceptAsync` does.

It carries `Abort(long errorCode, Exception? reason = null)`, which resets the whole stream with a code where
the transport carries one:

| Transport | What the reset sends |
|---|---|
| QUIC | `RESET_STREAM` and `STOP_SENDING` |
| WebTransport | both directions |
| HTTP/2 | `RST_STREAM` |
| SSH, yamux, mplex | reset without a code |

It then ends the lifecycle. A direction that already ended keeps its first code. It replaces HTTP/3's
three-step `ResetStream`.

- **Per-direction aborts stay the `StreamAbort` feature.** Only QUIC, WebTransport and the in-memory driver,
  which models QUIC, can end one direction alone. HTTP/3 keeps its no-code fallback, keyed on `DirectionalAbort`.
- **The guided base costs an in-memory restructure [Certain].** The in-memory `StreamEnd` inherits
  `InMemoryConnection`, so it becomes a `MultiplexedStream` that composes the in-memory connection's mechanics.

### (C) Input completion moves to the root; limits stay a feature

- `IMultiplexedStream.IsInputCompleted` replaces the former input-completion facet. It is a plain member: every
  transport can answer it, and `false` means "not known yet".
- The `StreamLimits` feature stays a feature.
  - QUIC's `MAX_STREAMS` and WebTransport's `WT_MAX_STREAMS` can implement it.
  - HTTP/2's `MAX_CONCURRENT_STREAMS` must not, because a concurrency limit is a different quantity.
- No generic limits type on the root. Every limit surveyed is transport-specific.

### (D) `ITlsConnectionInfo` is the `Tls` feature

Part 1 makes it the `Connections.Tls` package. TLS is added by a layer, and a cleartext connection cannot answer
it.

### (E) #1451 is folded in

(A) makes the in-memory driver honor a close code, and the only way to honor one is to tell the peer. When either
end of an in-memory multiplexed pair aborts or disposes:
- the other end's `ConnectionClosed` fires and its accept loop completes;
- its open streams fail as a connection loss, observing the code the way the driver already reports stream codes
  (`ConnectionResetException.ApplicationErrorCode`), unless the session documents a better mapping.

#1451 keeps its own commit.

### (F) `StreamId`: deferred

- Every transport in §3 numbers its streams, so a `long StreamId` would be a general root member later.
- **[Certain]** HTTP/3 derives request stream IDs by counting accepts (`Http3ConnectionContext.cs:406`, `:501`).
- **[Likely]** That is right as long as the driver delivers streams in ID order.

### (G) Unidirectional streams on a bidirectional-only transport

- Document that such a transport throws `NotSupportedException` for `OpenStreamAsync(WriteOnly)`, and never
  returns a `ReadOnly` stream from `AcceptStreamAsync`.
- A capability flag waits until a bidirectional-only driver exists.

## 8. Target contract after both parts

```csharp
public interface IMultiplexedConnection : IAsyncDisposable
{
    // Id, LocalEndPoint, RemoteEndPoint, Capabilities, State, ConnectionClosed: unchanged.
    IConnectionFeatureCollection Features { get; }                             // Part 1
    ValueTask<IMultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default);   // (B)
    ValueTask<IMultiplexedStream> OpenStreamAsync(
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        CancellationToken cancellationToken = default);                       // (B); (G) documents WriteOnly
    void Abort(Exception? reason = null);
    void Abort(long errorCode, Exception? reason = null);                     // (A)
}

public interface IMultiplexedStream : IConnection                             // IConnection carries Features (Part 1)
{
    bool IsInputCompleted { get; }                                             // (C)
    void Abort(long errorCode, Exception? reason = null);                     // (B)
}
```

The former coded-connection facet and the former input-completion facet are deleted. `ITlsConnectionInfo`,
`IMultiplexedStreamAbort` and `IMultiplexedConnectionStreamLimits` live in their feature packages.

## 9. Migration and tests

**Part 1 (#1452):**
- **Connections:** the core of §6.1, implemented on the three base classes, with tests for:
  - pass-through;
  - replacement by name;
  - `Remove` hiding an inner feature;
  - `Get<TFeature>()` order;
  - lazy creation, including that no collection is created until first access.
- **The three packages:** created and wired in (solutions, `library-connections.yml`, `CohesionPackaging.psm1`, App
  kernel roots, `docs/DEPENDENCIES.md`, `libraries/Connections/README.md`). Each gets tests for its extension
  property and pinned name, plus `OVERVIEW.md` and `DESIGN.md`.
- **Drivers and layers register explicitly:**
  - `TlsConnection` registers `Tls` and passes inner features through;
  - `QuicMultiplexedConnection` registers `Tls` and `StreamLimits`;
  - QUIC and in-memory streams register `StreamAbort`.
- **Consumers.**
  - Every type test on the three contracts in source becomes an extension property: HTTP's TLS reads and the
    HTTP/3 sites in §5.
  - Http.Tls still reads HTTP's own connection-info object.
- **Test wrappers** drop hand forwarding for `InnerFeatures`.

**Part 2 (#1453):**
- **Connections:** add `IMultiplexedStream` and `MultiplexedStream`, change the multiplexed root and its base,
  and delete the former coded-connection facet and the former input-completion facet. Extend `GuidedBaseTests`.
- **Connections.Quic:** `QuicStreamConnection` derives `MultiplexedStream`. Its root `Abort(code, reason)` ends
  both directions with the code, then the lifecycle. The connection-abort and input-completion tests move to the
  root members over real QUIC.
- **Connections.InMemory:** the `StreamEnd` composition, the root members, and (E).
- **Http.Connections:** the "After Part 2" column of §5. `DirectionalAbort` narrows to `IMultiplexedStream`.
- **Web.Hosting, Amqp and Connections test doubles:** implement the root members.

**Gates for both parts:**
- No source or test type-tests a connection for a feature contract.
- After Part 2, nothing names the former coded-connection facet or the former input-completion facet.
- The public surface diff is exactly §6 and §8, and no `InternalsVisibleTo` is added.
- The suites pass: Connections and its drivers (Connections.Quic over real QUIC, no skips), the three new
  packages, Http.Connections (with `--blame-hang-timeout 5m`), Http.Tls, Web.Hosting (three runs),
  Web.WebSockets, Web.Api, Web.Testing, the Amqp suites, and the App.Runtime closure tests.
- The 240 B input-completion allocation test and the interceptor allocation tests hold.
- The AOT guard smoke passes, and NativeAOT publishes clean for win-arm64.

## 10. Breaking changes against 10.0.0-preview.1

Recorded on decision 15's terms:
- **`Features`** on `IConnection`, `IMultiplexedConnection` and `IDatagramConnection` (Part 1) breaks implementers.
  The base classes cover subclasses.
- **Stream return types (Part 2).** `IMultiplexedConnection.AcceptStreamAsync` and `OpenStreamAsync` return
  `ValueTask<IMultiplexedStream>`, was `ValueTask<IConnection>`.
  - Callers that assign to `IConnection` still compile, but binaries break.
  - Implementers break.
- **The base's typed stream members (Part 2)** return `ValueTask<MultiplexedStream>`, was `ValueTask<Connection>`.
- **`Abort(long, Exception?)` (Part 2)** on `IMultiplexedConnection` and `MultiplexedConnection` breaks
  implementers.
- **Everything moved, renamed or deleted** postdates preview.1, so it breaks nothing released.

## 11. Docs

- **Connections DESIGN:**
  - **Part 1:**
    - a "Connection features" section: the collection, the lookup rules, layering, and the feature-package
      pattern;
    - the "no `Items` bag" non-goal rewritten as an owner decision;
    - the `ITlsConnectionInfo` section pointing to `Connections.Tls`.
  - **Part 2:**
    - "The Contract at a Glance", where multiplexed connections yield `IMultiplexedStream`;
    - "Guided Abstract Bases" gains `MultiplexedStream`;
    - "What every multiplexed transport supports, and what only some do", with §3's table and the rule;
    - (G)'s `WriteOnly` behavior.
- **Package docs:** `OVERVIEW.md` and `DESIGN.md` for the three new packages; the Connections OVERVIEW and README;
  the Connections.Security, Connections.Quic and Connections.InMemory DESIGN and OVERVIEW (#1451 in the last); the
  Http.Connections, Http and Http.Tls DESIGNs.
- **Site:** the cohesion-docs pages, in the stage-end docs sync.

## 12. Placement

- **Sessions.** Each part runs as one Codex session (implement, review, skeptics, fix), in order. Part 2 starts
  on Part 1's landed tip, and #1300 (s7-transport) starts on Part 2's.
- **Beside wave 2.** **[Likely]** Wave 2's other seven sessions keep running beside them: none edits Connections
  or Http.Connections source. The overlap is test code. s14-content edits Web.WebSockets tests that use the
  in-memory multiplexed driver.
- **Commits:**
  - Part 1: the core; each package with the drivers and consumers that adopt it; then docs. Each commit builds.
  - Part 2: the contract change with every consumer, double and DESIGN; then #1451; then any remaining docs.

## 13. Risks

- **A driver that implements a contract but never registers it offers nothing.** Each driver's tests assert
  what `Features` returns.
- **#1451 changes in-memory timing.** Suites that notify the peer by hand today may see the close earlier. Every
  suite in §9 runs.
- **A test double's root `Abort(code, reason)` must end the stream.** One that only records the code would leave
  HTTP/3 waiting. The member is abstract, and the session checks each double's behavior under hang timeouts.
- **Implementers of preview.1's interfaces outside this repository break.** That is accepted during the previews
  and recorded in §10.
