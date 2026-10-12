# Multiplexed Connection Contracts: Scope

**Date:** 2026-10-12. **Status:** scoped, awaiting owner review of decisions (B) correction, (C), (E) and (F).
**Area:** Connections (epic #324). **Program:** HTTP/Web (`docs/programs/HTTP_WEB_PROGRAM_PLAN.md`), inserted into
Stage 14 ahead of #1300. **Folds in:** #1451.

Claims that are not plain from the code are tagged **[Certain]** (checked against the code at `abe3751cf`),
**[Likely]** (a strong inference) or **[Guessing]** (a gap filled by judgment).

## 1. The owner's asks (2026-10-12)

1. Roll `IMultiplexedStreamAbort` and `IMultiplexedConnectionAbort` into `IMultiplexedConnection`. Revised after
   review: the connection abort moves to the connection, and the stream aborts move to a new root stream
   contract, because they act on one stream. The owner accepted that.
2. Explain `IConnectionInputCompletion`, and consolidate it: could item 3's type become generic enough to
   carry input completion too?
3. Replace `IMultiplexedConnectionStreamLimits` with a generic type on the root. Accepted, made more generic
   per item 2.

## 2. Findings first

- **[Certain] None of the four facets shipped in 10.0.0-preview.1.**

  | Facet | Added | Issue |
  |---|---|---|
  | `IMultiplexedStreamAbort`, `IMultiplexedConnectionAbort` | `83dd297e`, 2026-10-09 | #1080 |
  | `IConnectionInputCompletion` | `5a2ce987`, 2026-10-12 | #1427 |
  | `IMultiplexedConnectionStreamLimits` | `3c0e5c6e`, 2026-10-12 | #1425 |

  Deleting them breaks no released API. What preview.1 did ship is `IMultiplexedConnection` and
  `MultiplexedConnection`, whose `AcceptStreamAsync` and `OpenStreamAsync` return `IConnection` and `Connection`.
  Changing those, and adding members, breaks preview.1 implementers (§8).
- **The recorded reason for facets no longer holds for multiplexed contracts.** The Connections DESIGN says a
  member "breaks every implementation outside this repository" and "only a transport with codes can honor it: a
  TCP connection has none to send". TCP has no multiplexed connection. **[Certain]** Both multiplexed drivers,
  QUIC and in-memory, already carry codes. The first half is the cost the owner accepts during the previews.
- **[Certain] Facets fail silently through decorators.** A decorator hides a facet unless it implements and
  forwards it by hand.
  - Five test stream decorators forward by hand. `StreamInputConnection` and `CompletedInputConnection` forward
    two facets each.
  - The three test multiplexed decorators forward neither connection facet. That is harmless only because they
    wrap the in-memory driver, which implements neither.
  - Over a decorator that forgot, HTTP/3 falls back without a word. A root member turns the forgotten forward
    into a compile error.
- **[Certain] HTTP/3 uses facet presence as a capability switch.** `Http3RequestBodyStream.cs:528`, `:540` and
  `:1038` keep a cancellation-token fallback for streams that cannot carry a code. Once every stream carries
  codes, that fallback and its branches go.
- **Input completion is a stream fact; the stream allowance is a connection fact.** That difference decides
  (C).
- **[Certain] Correction to the first answer.** It proposed the stream contract as an interface only. The
  repository rule "Interface-first with a guided abstract base" (`general-rules.md`) and the Connections DESIGN
  ("each contract ships a public abstract base") call for a base too. (B) adds it.

## 3. What `IConnectionInputCompletion` does

It tells HTTP/3 that the peer's FIN has already arrived, without another read (#1427).

- **[Certain] The gap.** QUIC's input is `PipeReader.Create(QuicStream)`, which learns of a FIN only when a read
  returns 0. A GET whose FIN arrived with its HEADERS still looked as if a body could follow, so almost every
  HTTP/3 request reported `CanHaveBody = true`.
- **The cost.** Each such request got the client-fault gate's response sink, control, context and feature (240 B
  per bodiless request, measured in `Http3InputCompletionAllocationTests`), and automatic `100 Continue` was armed.
- **The mechanism.** QUIC answers `!_readAborted && ReadsClosed.IsCompletedSuccessfully`.
  `Http3RequestStreamReader.IsEndBuffered` samples it before probing the pipe, and trusts it only when the pipe
  buffer is empty. `false` means only "not known yet".
- **The alternative, not taken.** A read-ahead pump would make the pipe report the FIN itself. **[Likely]** That
  costs a task and a buffer per stream, more than the 240 B it saves.

## 4. Inventory at `abe3751cf`

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

| Site | Today |
|---|---|
| `Http3ConnectionContext.cs:962` | `is IMultiplexedConnectionAbort`, else `Abort(reason)` |
| `Http3ConnectionContext.cs:1006`, `:1024` | `is IMultiplexedStreamAbort` in `ResetStream` and `StopReadingWithCode` |
| `Http3ConnectionContext.cs:1516` | `is IMultiplexedConnectionStreamLimits` for the `PRIORITY_UPDATE` bound |
| `Http3ConnectionContext.cs:2062`, `:542`, `:561` | accepted and opened streams typed `IConnection` |
| `Http3ConnectionContext.cs:2153`, `Http3RequestStreamReader.cs:48` | `as IConnectionInputCompletion` |
| `Http3RequestBodyStream.cs:528`, `:540`, `:1038` | the no-code cancellation-token fallback |

**[Likely]** Amqp's `AmqpMultiplexedTransportConnection` holds streams as `IConnection` and compiles unchanged.

**Tests that run over the in-memory multiplexed driver outside Connections:** Http.Connections, Web.Hosting,
Web.WebSockets and Web.Api. #1451 changes that driver's close behavior, so all four suites run.

**Docs:**
- Connections DESIGN, OVERVIEW and README;
- Connections.Quic DESIGN and OVERVIEW, plus the XML docs on its two options types;
- Connections.InMemory DESIGN and OVERVIEW;
- Http.Connections DESIGN and Http DESIGN;
- ten cohesion-docs pages.

## 5. Decisions

### (A) The connection abort with a code moves to the root. Approved 2026-10-12.

`IMultiplexedConnection.Abort(long errorCode, Exception? reason = null)`, abstract on `MultiplexedConnection`.
- The semantics are `IMultiplexedConnectionAbort`'s:
  - the first abort or disposal decides the close code;
  - codes run from 0 to 2^62 - 1, and anything else throws `ArgumentOutOfRangeException`.
- The in-memory driver must now deliver the code to its peer. That is #1451's missing signal, so (E) folds it in.
- `IMultiplexedConnectionAbort` is deleted. HTTP/3's `AbortConnection` calls the root member unconditionally.

### (B) Stream aborts move to a root stream contract. Approved, with one correction.

`IMultiplexedStream : IConnection` holds `AbortRead(long)` and `AbortWrite(long)` with `IMultiplexedStreamAbort`'s
semantics. Each direction ends once, and the first signal wins. `AcceptStreamAsync` and `OpenStreamAsync`
return it.

**Correction: it ships with a guided base.** `public abstract class MultiplexedStream : Connection, IMultiplexedStream`.
`MultiplexedConnection`'s typed members return `ValueTask<MultiplexedStream>`, and the interface members forward,
as `ConnectionListener.AcceptAsync` does.
- **Why:** the repository rule and the family's convention. Every Connections contract has one, and a driver
  author gets the same guidance as for `Connection`.
- **Cost [Certain]:** the in-memory `StreamEnd` inherits `InMemoryConnection`, so it cannot also derive from
  `MultiplexedStream`. The stream end becomes a `MultiplexedStream` that composes the in-memory connection's
  mechanics. The driver is internal, so nothing outside the package changes.
- **Alternative:** interface only, as first proposed. It has no restructure, but it is the one Connections
  contract without a base, and that deviation has to be recorded.

**Rejected: stream aborts on the connection** (`AbortStream(IConnection, long)`).
- **[Certain]** `ResetStream` and `StopReadingWithCode` are static and hold only the stream.
- The connection would need an ownership check. **[Likely]** The in-memory driver keeps no stream registry to
  check against.

### (C) One generic type for what the transport observes. Owner's item 2; needs a decision.

The owner asked whether item 3's type can be generic enough to carry input completion. It can share a pattern,
but not a type: one fact belongs to a stream, the other to its connection.

| | C1, one shared type (the literal ask) | **C2, one family, one type per level (recommended)** | C3, a typed key bag |
|---|---|---|---|
| Shape | `MultiplexedFlow { InboundBidirectionalStreamLimit, IsInputCompleted }` on both roots | `MultiplexedConnectionFlow` on `IMultiplexedConnection.Flow`; `MultiplexedStreamFlow` on `IMultiplexedStream.Flow` | `TryGetValue<T>(ConnectionValue<T>, out T)` on both roots |
| New types | 1 | 2 | 2 or more |
| Level safety | none: a stream reports the connection limit as `null` | the compiler rejects reading a connection fact off a stream | none, and checked only at run time |
| Hot path | a struct copy | a struct copy | **[Likely]** a generic virtual dispatch per request under NativeAOT |

- **The risk in C1 is specific.** `null` means "no bound reported", and the `PRIORITY_UPDATE` check skips itself
  on `null`. That check closed a preview.1 memory-exhaustion hole: `PRIORITY_UPDATE` state grew without bound
  until `0446aa80`. Read off the wrong object, C1 turns it off silently. Each C1 member is also meaningless at one
  of the two levels: a connection's `IsInputCompleted` is always `false`.
- **C3 is ruled out three ways:**
  - the Connections DESIGN's Non-Goals already exclude "a general-purpose `Items` property bag on the connection";
  - every transport would hand-cast per key;
  - it brings back discovery by key, the type tests this change removes.
- **C2 still consolidates.** Four facet interfaces become root members. There is one pattern: a snapshot struct
  named `Flow` on each root, the dynamic counterpart of the static `ConnectionCapabilities`. Each struct grows by
  `init` properties without breaking callers.
  - **[Certain]** Future connection facts have a home: outbound stream credit, which .NET 10 reports through
    `QuicConnectionOptions.StreamCapacityCallback`, and the unidirectional allowances.
  - **[Likely]** So do future stream facts, such as delivery acknowledged (QUIC `WritesClosed`).
  - Nothing is added before a consumer needs it.

C2's two structs:

| Member | Struct | Meaning | Reported by |
|---|---|---|---|
| `InboundBidirectionalStreamLimit` (`long?`) | `MultiplexedConnectionFlow` | cumulative, nondecreasing, never understated; `null` when the transport reports none | QUIC; in-memory reports `null` |
| `IsInputCompleted` (`bool`) | `MultiplexedStreamFlow` | the peer ended input cleanly and every received byte reached `Input`; `false` means not known yet | QUIC; in-memory when its peer completed output cleanly |

**Naming is the owner's call.** `Flow` covers both credit and end of input, and it does not collide with the
lifecycle `State`. The alternatives are `Status`, which sits next to `State`, and `Limits`, which cannot hold
input completion.

### (D) `ITlsConnectionInfo` stays a facet. Recommended.

- TLS is added by a layer: `TlsConnection` in Connections.Security decorates a plain connection.
- **[Certain]** A non-connection type implements it too: `HttpTlsConnectionInfo` in Http.Connections.
- A cleartext connection cannot answer it, and putting it on `IConnection` would make every driver and dozens of
  test doubles answer `null`.

This is the rule the change applies: a member belongs on a root when every implementation can honor it, and a
facet stays when some transports genuinely cannot.

### (E) Fold #1451 into the same change. Recommended.

(A) makes the in-memory driver honor a close code, and the only way to honor one is to tell the peer. When either
end of an in-memory multiplexed pair aborts or disposes:
- the other end's `ConnectionClosed` fires and its accept loop completes;
- its open streams fail as a connection loss, observing the code the way the driver already reports stream codes
  (`ConnectionResetException.ApplicationErrorCode`), unless the session documents a better mapping.

#1451 keeps its own issue and commit.

### (F) `StreamId` on `IMultiplexedStream`. Recommended: defer.

- **[Certain]** HTTP/3 derives request stream IDs by counting accepts (`Http3ConnectionContext.cs:406`, `:501`).
- **[Likely]** That is right as long as the driver delivers streams in ID order, which RFC 9000 §3.2's implicit
  opening implies.
- Adding the member later breaks implementers, as any addition does. Add it when a defect or consumer asks.

## 6. Target contract (C2)

```csharp
public interface IMultiplexedConnection : IAsyncDisposable
{
    // Id, LocalEndPoint, RemoteEndPoint, Capabilities, State, ConnectionClosed: unchanged.
    MultiplexedConnectionFlow Flow { get; }                                    // (C), replaces IMultiplexedConnectionStreamLimits
    ValueTask<IMultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default);   // (B), was IConnection
    ValueTask<IMultiplexedStream> OpenStreamAsync(
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        CancellationToken cancellationToken = default);                       // (B), was IConnection
    void Abort(Exception? reason = null);
    void Abort(long errorCode, Exception? reason = null);                     // (A), replaces IMultiplexedConnectionAbort
}

public interface IMultiplexedStream : IConnection
{
    MultiplexedStreamFlow Flow { get; }                                        // (C), replaces IConnectionInputCompletion
    void AbortRead(long errorCode);                                            // (B), replaces IMultiplexedStreamAbort
    void AbortWrite(long errorCode);
}

public readonly record struct MultiplexedConnectionFlow
{
    public long? InboundBidirectionalStreamLimit { get; init; }
}

public readonly record struct MultiplexedStreamFlow
{
    public bool IsInputCompleted { get; init; }
}
```

The guided bases are `MultiplexedStream : Connection, IMultiplexedStream`, with abstract `Flow`, `AbortRead` and
`AbortWrite`, and `MultiplexedConnection`, with abstract `Flow`, `Abort(long, Exception?)` and typed
`ValueTask<MultiplexedStream>` stream members. Each `Flow` read is a snapshot sampled when read, and its members
are sampled independently.

## 7. Migration and tests

**Per project:**
- **Connections:** add the four types and change the root interface and its base. Delete the four facets. Extend
  `GuidedBaseTests` to `MultiplexedStream` forwarding and the new members.
- **Connections.Quic:** `QuicStreamConnection` derives `MultiplexedStream`. Its `Flow` comes from `ReadsClosed`.
  `QuicMultiplexedConnection.Flow` replaces `InboundBidirectionalStreamLimit`. The facet tests (24 references) move
  to the root members, over real QUIC.
- **Connections.InMemory:** the `StreamEnd` composition from (B), the code delivery and peer signal from (E), and
  `Flow`. The tests asserting that a plain in-memory connection lacks the stream facet become "is not an
  `IMultiplexedStream`".
- **Http.Connections:**
  - Every site in §4 calls the root members. The `Http3RequestBodyStream` fallback branches are deleted.
  - The test doubles implement the root members. Queued `TestConnection` streams go through one shared test
    stream that honors the contract: `AbortRead` fails a read in flight.
  - A test that pinned the no-code fallback is deleted only where its branch is, and the session lists it.
- **Web.Hosting, Amqp and Connections tests:** their doubles implement the root members.

**Gates:**
- No source or test names the four deleted types.
- The public surface diff is exactly the types in §6, and no `InternalsVisibleTo` is added.
- The suites pass: Connections, Connections.Quic (real QUIC, no skips), Connections.InMemory, Http.Connections
  (with `--blame-hang-timeout 5m`), Web.Hosting (three runs), Web.WebSockets, Web.Api, Web.Testing and the Amqp
  suites.
- The 240 B input-completion allocation test and the interceptor allocation tests hold.
- The AOT guard smoke passes, and NativeAOT publishes clean for win-arm64.

## 8. Breaking changes against 10.0.0-preview.1

Recorded on decision 15's terms:
- `IMultiplexedConnection.AcceptStreamAsync` and `OpenStreamAsync` return `ValueTask<IMultiplexedStream>`, was
  `ValueTask<IConnection>`.
  - **Callers:** source compatible where they assign to `IConnection`, but binary breaking.
  - **Implementers:** breaking.
- `MultiplexedConnection`'s typed stream members return `ValueTask<MultiplexedStream>`, was
  `ValueTask<Connection>`.
- New members `Flow` and `Abort(long, Exception?)` on `IMultiplexedConnection` and `MultiplexedConnection` break
  implementers.
- The deleted facets, and `QuicMultiplexedConnection.InboundBidirectionalStreamLimit`, postdate preview.1, so
  they break nothing released.

## 9. Docs

- **Connections DESIGN:**
  - "The Contract at a Glance": multiplexed connections yield `IMultiplexedStream`.
  - "Guided Abstract Bases" adds `MultiplexedStream`.
  - The three facet sections become one section on codes and `Flow` on the roots. It records why the "Facets,
    not members" reasoning no longer holds, and keeps the `ITlsConnectionInfo` section with (D)'s reason.
  - Non-Goals are unchanged and cited by (C).
- **Package docs:** the Connections OVERVIEW and README; the Connections.Quic DESIGN, OVERVIEW and options XML
  docs; the Connections.InMemory DESIGN and OVERVIEW (with #1451); the Http.Connections and Http DESIGNs.
- **Site:** the ten cohesion-docs pages, in the stage-end docs sync.
- **Plan:** the owner's answers become decision 56 in `HTTP_WEB_PROGRAM_PLAN.md` §7.4, with the Stage 14 insertion.

## 10. Work breakdown and placement

- **Items:**
  - One feature under #324: `[L01.01.14.18] Put codes, streams and flow on the multiplexed connection roots`,
    covering (A), (B) and (C).
  - #1451 (`[L01.01.14.17]`), filed 2026-10-12, folded in as its own commit.
- **One Codex session** (implement, review, skeptics, fix) on the branch tip at approval.
  - **[Likely]** It can run beside wave 2's remaining sessions: none edits Connections or Http.Connections
    source.
  - The overlap is test code. s14-content edits Web.WebSockets tests that use the in-memory multiplexed driver.
- **Commits:**
  1. The contract change, with every consumer, double and DESIGN, so each commit builds.
  2. #1451.
  3. Any remaining docs.
- **Sequence:**
  1. Land this session first.
  2. #1300 (s7-transport) starts on top of it, because it instruments exactly the HTTP/3 abort sites in §4.
  3. Wave 2's integration then cherry-picks each session from its own base.

## 11. Risks

- **Test doubles that do not honor `AbortRead`.** The no-code fallback cancelled reads with a token. Without it,
  a double that ignores `AbortRead` hangs a test instead of failing it. Mitigations: one shared test stream, and
  hang timeouts.
- **#1451 changes in-memory timing.** Suites that notify the peer by hand today may see the close earlier. Every
  suite in §7 runs.
- **Implementers of preview.1's `IMultiplexedConnection` outside this repository break.** That is accepted
  during the previews and recorded in §8.
