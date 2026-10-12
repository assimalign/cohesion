# Multiplexed Connection Contracts: Scope

**Date:** 2026-10-12, revised the same day after the owner's transport-generality check (§3).
**Status:** scoped, awaiting owner sign-off on §6.
**Area:** Connections (epic #324). **Program:** HTTP/Web (`docs/programs/HTTP_WEB_PROGRAM_PLAN.md`), inserted into
Stage 14 ahead of #1300. **Folds in:** #1451.

Claims that are not plain from the code are tagged **[Certain]** (checked against the code at `abe3751cf`, or
against the specification cited), **[Likely]** (a strong inference) or **[Guessing]** (a gap filled by judgment).

## 1. The owner's asks (2026-10-12)

1. Roll `IMultiplexedStreamAbort` and `IMultiplexedConnectionAbort` into the multiplexed root contract.
2. Explain `IConnectionInputCompletion`, and consolidate it if possible.
3. Replace `IMultiplexedConnectionStreamLimits` with a generic type on the root, generic enough to carry input
   completion too.
4. Before sign-off, make sure none of it is shaped only for HTTP and QUIC. Other multiplexed transports must be
   able to implement the roots without dead members or conflicting semantics.

## 2. Findings first

- **[Certain] Ask 4 overturns two of the first recommendations.** The first draft judged each member against
  the two drivers in this repository, QUIC and in-memory, and both carry codes per direction. The transport
  survey in §3 shows that is a QUIC-family property:
  - **Per-direction aborts with codes:** HTTP/2, SSH, yamux and mplex cannot abort one direction alone. SSH,
    yamux and mplex carry no stream code at all. On the root, these would be members four of six transports
    cannot honor.
  - **The cumulative stream allowance:** only QUIC and WebTransport have one. HTTP/2's limit is a concurrency
    limit, and SSH, yamux and mplex have none. A root limits type would carry one QUIC field that four of six
    transports leave empty.
- **What every byte-stream multiplexer does have:**
  - a half-close, so input completion is observable;
  - a whole-stream reset;
  - a connection close.
  
  Most can also put a code on the reset and on the close. Those go on the roots.
- **[Certain] None of the four facets shipped in 10.0.0-preview.1.**

  | Facet | Added | Issue |
  |---|---|---|
  | `IMultiplexedStreamAbort`, `IMultiplexedConnectionAbort` | `83dd297e`, 2026-10-09 | #1080 |
  | `IConnectionInputCompletion` | `5a2ce987`, 2026-10-12 | #1427 |
  | `IMultiplexedConnectionStreamLimits` | `3c0e5c6e`, 2026-10-12 | #1425 |

  Deleting or reshaping them breaks nothing released. Preview.1 did ship `IMultiplexedConnection` and
  `MultiplexedConnection`, so new members and new stream return types break preview.1 implementers (§9).
- **[Certain] The existing root already has one QUIC-family member.** `OpenStreamAsync(ConnectionDirection.WriteOnly)`
  opens a unidirectional stream, which HTTP/2, SSH, yamux and mplex do not have. (G) settles what such a
  transport does.
- **[Certain] Facets fail silently through decorators.** A decorator hides a facet unless it implements and
  forwards it by hand. Five test stream decorators forward by hand, and the three test multiplexed decorators
  forward neither connection facet. That is harmless only because they wrap the in-memory driver. A root member
  turns a forgotten forward into a compile error. That is the case for moving a member to a root wherever every
  transport can honor it.

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

**The rule this scope applies.** A member goes on a root only when every transport in the table can honor its
documented semantics. A capability only some transports have stays a facet, found with a type test. A member
that most transports would implement as a no-op, or by breaking its own documentation, is the dead member this
change must not create.

## 4. What `IConnectionInputCompletion` does

It tells HTTP/3 that the peer's FIN has already arrived, without another read (#1427).

- **[Certain] The gap.** QUIC's input is `PipeReader.Create(QuicStream)`, which learns of a FIN only when a read
  returns 0. A GET whose FIN arrived with its HEADERS still looked as if a body could follow, so almost every
  HTTP/3 request reported `CanHaveBody = true`.
- **The cost.** Each such request got the client-fault gate's response sink, control, context and feature (240 B
  per bodiless request, measured in Web.Hosting's `Http3InputCompletionAllocationTests`), and automatic
  `100 Continue` was armed.
- **The mechanism.** QUIC answers `!_readAborted && ReadsClosed.IsCompletedSuccessfully`.
  `Http3RequestStreamReader.IsEndBuffered` samples it before probing the pipe, and trusts it only when the pipe
  buffer is empty. `false` means only "not known yet".
- **Generality.** Every transport in §3 has a half-close, so every stream can answer, with `false` as the
  honest default. It belongs on the root stream contract.

## 5. Inventory at `abe3751cf`

The four facets appear in 42 files (135 occurrences, docs included).

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
  Web.Hosting's `CompletedInputConnection` and `ResponseConnection`. **[Likely]** Compiling finds the rest.

**Consumers in source (all in HTTP/3).** All of these are in `libraries/Http/Assimalign.Cohesion.Http.Connections/src/Internal/Http3/`.

| Site | Today | After |
|---|---|---|
| `Http3ConnectionContext.cs:962` | `is IMultiplexedConnectionAbort`, else `Abort(reason)` | root `Abort(code, reason)` |
| `Http3ConnectionContext.cs:1006` (`ResetStream`) | per-direction facet, then `Abort(reason)` | root stream `Abort(code, reason)` |
| `Http3ConnectionContext.cs:1024` (`StopReadingWithCode`) | per-direction facet | unchanged, QUIC-family facet |
| `Http3ConnectionContext.cs:1516` | allowance facet for the `PRIORITY_UPDATE` bound | unchanged, QUIC-family facet |
| `Http3ConnectionContext.cs:2062`, `:542`, `:561` | streams typed `IConnection` | `IMultiplexedStream` |
| `Http3ConnectionContext.cs:2153`, `Http3RequestStreamReader.cs:48` | `as IConnectionInputCompletion` | root `IsInputCompleted` |
| `Http3RequestBodyStream.cs:528`, `:540`, `:1038` | no-code fallback for streams without the per-direction facet | unchanged |

**[Likely]** Amqp's `AmqpMultiplexedTransportConnection` holds streams as `IConnection` and compiles unchanged.

**Tests that run over the in-memory multiplexed driver outside Connections:** Http.Connections, Web.Hosting,
Web.WebSockets and Web.Api. #1451 changes that driver's close behavior, so all four suites run.

**Docs:**
- Connections DESIGN, OVERVIEW and README;
- Connections.Quic DESIGN and OVERVIEW, plus the XML docs on its two options types;
- Connections.InMemory DESIGN and OVERVIEW;
- Http.Connections DESIGN and Http DESIGN;
- ten cohesion-docs pages.

## 6. Decisions for sign-off

### (A) The connection close with a code moves to the root

`IMultiplexedConnection.Abort(long errorCode, Exception? reason = null)`, abstract on `MultiplexedConnection`.
It replaces `IMultiplexedConnectionAbort`.

- **Generality:** five of the six transports carry a close code (§3).
- **The code space is the transport's, not QUIC's.** The root contract does not fix 2^62 - 1. QUIC takes 0 to
  2^62 - 1; WebTransport, HTTP/2 and SSH take 32-bit codes. A driver throws `ArgumentOutOfRangeException` for a
  code it cannot carry, and a negative code is never valid.
- **A transport whose close carries no code** (mplex) closes as `Abort(reason)` does. The close still happens,
  so the member is not dead; only the code is dropped, which its documentation says.
- **Kept semantics:** the first abort or disposal decides the close.

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

It then ends the stream's lifecycle, as `Abort(reason)` does. A direction that already ended keeps its first
code. Every transport can honor this, and it replaces HTTP/3's three-step `ResetStream`.

**Per-direction aborts stay a facet.** `IMultiplexedStreamAbort` (`AbortRead`, `AbortWrite`) keeps its shape and
its semantics: one direction ends, and the other is unaffected.
- **Who has it:** QUIC and WebTransport, and the in-memory driver, which models QUIC. HTTP/2, SSH, yamux and mplex
  do not.
- **Why it cannot go on the root:** `RST_STREAM`, `CHANNEL_CLOSE` and RST end both directions, so a root
  `AbortWrite` documented as "the receiving direction is unaffected" would be a lie on four of six transports.
- **What HTTP/3 keeps:** its type test and its fallback (`Http3RequestBodyStream.cs:528`, `:540`, `:1038`).
  HTTP/3 runs only over QUIC, where the facet is always present.

**Guided base cost [Certain]:** the in-memory `StreamEnd` inherits `InMemoryConnection`, so it becomes a
`MultiplexedStream` that composes the in-memory connection's mechanics. The driver is internal, so nothing
outside the package changes.

### (C) Input completion moves to the root; limits stay a facet; no generic type

- **`IMultiplexedStream.IsInputCompleted`** replaces `IConnectionInputCompletion`.
  - It is a plain member, not a struct: the struct had no second general member to carry.
  - Every transport in §3 can answer it, with `false` meaning "not known yet".
- **`IMultiplexedConnectionStreamLimits` stays a facet,** documented as a QUIC-family capability:
  - QUIC's `MAX_STREAMS` and WebTransport's `WT_MAX_STREAMS` can implement it;
  - HTTP/2's `MAX_CONCURRENT_STREAMS` must not, because a concurrency limit is a different quantity.
- **No generic limits or "flow" type on the root.** Every limit surveyed is transport-specific: cumulative
  allowances, concurrency limits, fixed counts, or none. A root type would always hold fields most transports
  leave empty.
  - **[Likely]** Revisit this when a limit appears that every transport has and a consumer reads.
  - The first draft's `Flow` structs (its option C2) are withdrawn.

### (D) `ITlsConnectionInfo` stays a facet

- TLS is added by a layer: `TlsConnection` in Connections.Security decorates a plain connection.
- **[Certain]** A non-connection type implements it too: `HttpTlsConnectionInfo` in Http.Connections.
- A cleartext connection cannot answer it.

### (E) Fold #1451 into the same change

(A) makes the in-memory driver honor a close code, and the only way to honor one is to tell the peer. When either
end of an in-memory multiplexed pair aborts or disposes:
- the other end's `ConnectionClosed` fires and its accept loop completes;
- its open streams fail as a connection loss, observing the code the way the driver already reports stream codes
  (`ConnectionResetException.ApplicationErrorCode`), unless the session documents a better mapping.

#1451 keeps its own issue and commit.

### (F) `StreamId` on `IMultiplexedStream`: defer

- **Generality:** every transport in §3 numbers its streams, from HTTP/2's 31 bits to mplex's 60 and QUIC's 62.
  SSH numbers each channel once per side. So a `long StreamId` would be a general root member, not a QUIC-ism.
- **Why not now:**
  - **[Certain]** HTTP/3 derives request stream IDs by counting accepts (`Http3ConnectionContext.cs:406`, `:501`).
  - **[Likely]** That is right as long as the driver delivers streams in ID order, which RFC 9000 §3.2's implicit
    opening implies.
  - Add it when a defect or a consumer asks.

### (G) Unidirectional streams on a bidirectional-only transport

Only QUIC and WebTransport have unidirectional streams. `IMultiplexedConnection.OpenStreamAsync` documents
`ArgumentException` for `ReadOnly` today and says nothing about `WriteOnly` on a transport without unidirectional
streams.
- **Recommended:** document that such a transport throws `NotSupportedException` for `WriteOnly`, and never
  returns a `ReadOnly` stream from `AcceptStreamAsync`.
- **Deferred:** a capability flag in `ConnectionCapabilities`, until a bidirectional-only driver exists.
- **[Likely]** HTTP/3 needs unidirectional streams, so over such a transport it fails at its first
  `OpenStreamAsync`, which is the right place.

## 7. Target contract

```csharp
public interface IMultiplexedConnection : IAsyncDisposable
{
    // Id, LocalEndPoint, RemoteEndPoint, Capabilities, State, ConnectionClosed: unchanged.
    ValueTask<IMultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default);   // (B), was IConnection
    ValueTask<IMultiplexedStream> OpenStreamAsync(
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        CancellationToken cancellationToken = default);                       // (B), was IConnection; (G) documents WriteOnly
    void Abort(Exception? reason = null);
    void Abort(long errorCode, Exception? reason = null);                     // (A), replaces IMultiplexedConnectionAbort
}

public interface IMultiplexedStream : IConnection
{
    bool IsInputCompleted { get; }                                             // (C), replaces IConnectionInputCompletion
    void Abort(long errorCode, Exception? reason = null);                     // (B), whole-stream reset with a code
}

// QUIC-family capabilities: unchanged in shape, still found with a type test.
public interface IMultiplexedStreamAbort { void AbortRead(long errorCode); void AbortWrite(long errorCode); }
public interface IMultiplexedConnectionStreamLimits { long InboundBidirectionalStreamLimit { get; } }
```

Guided bases:
- `MultiplexedStream : Connection, IMultiplexedStream`, with abstract `IsInputCompleted` and `Abort(long, Exception?)`.
- `MultiplexedConnection`, with abstract `Abort(long, Exception?)` and typed `ValueTask<MultiplexedStream>` stream
  members.

The net change: two facets become root members, two QUIC-family facets stay, and one stream contract plus its
base are added.

## 8. Migration and tests

**Per project:**
- **Connections:**
  - Add `IMultiplexedStream` and `MultiplexedStream`, and change the root interface and its base.
  - Delete `IMultiplexedConnectionAbort` and `IConnectionInputCompletion`.
  - Re-document the two facets that stay as QUIC-family capabilities, with the §3 table.
  - Extend `GuidedBaseTests` to `MultiplexedStream` forwarding and the new members.
- **Connections.Quic:**
  - `QuicStreamConnection` derives `MultiplexedStream` and keeps `IMultiplexedStreamAbort`. Its root
    `Abort(code, reason)` ends both directions with the code, then the lifecycle.
  - `QuicMultiplexedConnection.Abort(long, ...)` becomes an override, and it keeps the allowance facet.
  - The connection-abort and input-completion tests move to the root members, over real QUIC.
- **Connections.InMemory:**
  - The `StreamEnd` composition from (B), the root members, and the code delivery and peer signal from (E).
  - The test asserting that a byte-stream pair offers no code-carrying abort stays: it is still true.
- **Http.Connections:**
  - Apply the "After" column of §5.
  - The test doubles implement the root members. The per-direction facet stays optional on them, so the no-code
    fallback paths stay reachable and tested.
- **Web.Hosting, Amqp and Connections tests:** their doubles implement the root members.

**Gates:**
- No source or test names `IMultiplexedConnectionAbort` or `IConnectionInputCompletion`.
- The public surface diff is exactly §7, and no `InternalsVisibleTo` is added.
- The suites pass: Connections, Connections.Quic (real QUIC, no skips), Connections.InMemory, Http.Connections
  (with `--blame-hang-timeout 5m`), Web.Hosting (three runs), Web.WebSockets, Web.Api, Web.Testing and the Amqp
  suites.
- The 240 B input-completion allocation test and the interceptor allocation tests hold.
- The AOT guard smoke passes, and NativeAOT publishes clean for win-arm64.

## 9. Breaking changes against 10.0.0-preview.1

Recorded on decision 15's terms:
- `IMultiplexedConnection.AcceptStreamAsync` and `OpenStreamAsync` return `ValueTask<IMultiplexedStream>`, was
  `ValueTask<IConnection>`.
  - **Callers:** source compatible where they assign to `IConnection`, but binary breaking.
  - **Implementers:** breaking.
- `MultiplexedConnection`'s typed stream members return `ValueTask<MultiplexedStream>`, was
  `ValueTask<Connection>`.
- The new member `Abort(long, Exception?)` on `IMultiplexedConnection` and `MultiplexedConnection` breaks
  implementers.
- The deleted facets postdate preview.1, so they break nothing released.

## 10. Docs

- **Connections DESIGN:**
  - "The Contract at a Glance": multiplexed connections yield `IMultiplexedStream`.
  - "Guided Abstract Bases" adds `MultiplexedStream`.
  - A new section, "What every multiplexed transport supports, and what only some do", carries the §3 table and
    the rule.
  - The facet sections say why the coded close and input completion moved to the roots, and why per-direction
    aborts and the allowance stay QUIC-family facets.
  - The "Facets, not members" reasoning is replaced by the rule. (D)'s reason is kept, and (G)'s `WriteOnly`
    behavior is documented.
- **Package docs:** the Connections OVERVIEW and README; the Connections.Quic DESIGN, OVERVIEW and options XML
  docs; the Connections.InMemory DESIGN and OVERVIEW (with #1451); the Http.Connections and Http DESIGNs.
- **Site:** the ten cohesion-docs pages, in the stage-end docs sync.
- **Plan:** the owner's answers become decision 56 in `HTTP_WEB_PROGRAM_PLAN.md` §7.4, with the Stage 14 insertion.

## 11. Work breakdown and placement

- **Items:**
  - One feature under #324: `[L01.01.14.18] Put the members every multiplexed transport supports on the roots`,
    covering (A), (B), (C) and (G).
  - #1451 (`[L01.01.14.17]`), filed 2026-10-12, folded in as its own commit.
- **One Codex session** (implement, review, skeptics, fix) on the branch tip at sign-off.
  - **[Likely]** It can run beside wave 2's remaining sessions: none edits Connections or Http.Connections
    source.
  - The overlap is test code. s14-content edits Web.WebSockets tests that use the in-memory multiplexed driver.
- **Commits:**
  1. The contract change, with every consumer, double and DESIGN, so each commit builds.
  2. #1451.
  3. Any remaining docs.
- **Sequence:**
  1. Land this session first.
  2. #1300 (s7-transport) starts on top of it, because it instruments the HTTP/3 abort sites in §5.
  3. Wave 2's integration then cherry-picks each session from its own base.

## 12. Risks

- **#1451 changes in-memory timing.** Suites that notify the peer by hand today may see the close earlier. Every
  suite in §8 runs.
- **A test double's root `Abort(code, reason)` must end the stream.** A double that only records the code would
  leave HTTP/3 waiting. The base makes the member abstract, so no double can omit it. The session checks each
  one's behavior, under hang timeouts.
- **Implementers of preview.1's `IMultiplexedConnection` outside this repository break.** That is accepted
  during the previews and recorded in §9.
