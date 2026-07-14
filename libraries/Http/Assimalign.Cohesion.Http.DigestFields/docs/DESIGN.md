# Assimalign.Cohesion.Http.DigestFields — Design

RFC 9530 integrity digest fields — `Content-Digest`, `Repr-Digest`, `Want-Content-Digest`,
`Want-Repr-Digest` — for the Cohesion HTTP stack: a value model over the structured-fields
toolkit, `sha-256` / `sha-512` computation and verification via BCL incremental hashing, and an
opt-in server-side request verifier. The package references only core `Assimalign.Cohesion.Http`;
the transport never references this package.

## Why this package exists (seams vs. features)

RFC 9530 digest fields are RFC 9651 Structured Field **Dictionaries** (`algorithm=:base64:` for a
digest, `algorithm=preference` for a want-preference). Two facts from the core design pin the
placement of this code:

- **Core parses field *names*, not field *values*.** `HttpFieldRules` is name-classification only;
  the core `docs/DESIGN.md` names "Per-field parsers" as an explicit non-goal and says value
  parsing "belongs to the field-specific consumer, and the shared toolkit those consumers build on
  for RFC 9651 syntax is the structured-fields surface." This package is exactly that consumer: it
  builds on core's `StructuredFieldDictionary` and adds only the digest-specific schema on top.
- **Features live outside core.** Like `Http.Cookies`, `Http.Sessions`, and `Http.RequestLimits`,
  a concrete capability (here: integrity verification) ships in its own package that references
  only core. The one core change this feature needs — the four well-known `HttpHeaderKey` entries —
  is a name registration, not a parser, so it stays in core alongside the existing ~90 keys.

The result: core gained four header-name constants; everything else (the value model, the
algorithm registry, computation, verification, and the interceptor) lives here.

## The value model

`HttpDigestField` is the parsed value of a `Content-Digest` **or** `Repr-Digest` field, and
`HttpWantDigestField` the parsed value of a `Want-Content-Digest` **or** `Want-Repr-Digest` field.
One model serves both members of each pair because the syntax is identical — the only difference is
which header carries it and what the digest is taken over. Both are thin readonly-struct views over
the parsed `StructuredFieldDictionary`, which stays the round-trip source of truth (so `Serialize()`
reproduces every member, including ones the typed view skips).

Design points:

- **`TryParse` is the primary entry point** (span- and `HttpHeaderValue`-based, `out string? error`
  for diagnosis); `Parse` is a throwing convenience that raises the area-scoped
  `HttpDigestException`. This matches the structured-fields toolkit and the Lane-B "value objects
  with `TryParse`/serialize, span-based, AOT-safe" guardrail.
- **Malformed is rejected, not silently dropped.** Bad base64, missing colons, or unbalanced quotes
  fail in the structured-fields parser; a member whose value is not a Byte Sequence (for a digest)
  or not an Integer (for a want-preference) is rejected by the schema check with a diagnostic naming
  the offending key; an empty dictionary is rejected. Each surfaces through `TryParse` returning
  `false` with an `error`.
- **All entries are preserved.** A multi-algorithm value (`sha-256=:…:, sha-512=:…:`) keeps every
  entry in order. Deprecated algorithms are surfaced as typed `Entries` (so a recipient can *see*
  them) but flagged unsupported; unregistered-but-well-formed keys are preserved for round-tripping
  and omitted from the typed view, because a recipient cannot act on an unknown algorithm (RFC 9530
  says to ignore unknown algorithms — not to reject the whole field).

## The algorithm registry

`HttpDigestAlgorithm` models the RFC 9530 §5.2 registry as a small readonly struct keyed by the
lowercase registry token. It draws the line the RFC draws:

- **Active / supported** — `sha-256`, `sha-512`. `IsSupported` is `true`; these are the only
  algorithms used to **generate or validate** a digest.
- **Deprecated / recognized** — `md5`, `sha` (SHA-1), `unixsum`, `unixcksum`. `TryParse` recognizes
  them (so they can be enumerated and skipped), but `IsSupported` is `false` and any attempt to hash
  with one throws. This is the RFC 9530 §5 rule ("recognized on parse but never used") made
  type-level: `CreateIncrementalHash()` — the only path to a hash — exists only for supported
  algorithms.

There is deliberately no provider/string-name indirection: supported algorithms map to fixed
`HashAlgorithmName` values and BCL `IncrementalHash`, so the whole path is AOT-safe (no reflection,
no runtime codegen, no dynamic algorithm lookup).

## Computation and the incremental digester

Every hash goes through `IncrementalHash`, honoring the issue's "BCL incremental hashing, no
reflection" mandate literally:

- `HttpDigestField.ForContent(content, …algorithms)` computes a buffered digest for one or more
  algorithms.
- `HttpDigestField.ForContentAsync(stream, algorithms, ct)` reads a stream **once**, feeding every
  algorithm's running hash in parallel — the path a caller uses when the representation is only
  available as a stream.
- `HttpContentDigester` is the standalone "hash as you write" primitive: a server feeds each body
  chunk to `Append` as it writes it to the wire and calls `ToField()` when the body is complete.
  `ToField()` snapshots without resetting, so it can be called at end-of-body without preventing
  further appends. This is the primitive behind the **trailer-borne** case: for a streamed body the
  digest is not known until the body is written, so it is emitted in the trailer section. RFC 9530
  permits digest fields as trailers, and `HttpFieldRules.IsProhibitedInTrailers` does **not** list
  them (a guard test locks this) — so the field is trailer-eligible today even though wiring the
  actual trailer emission into a streaming response writer rides on the streaming write path (#769).

## Server-side verification (the interceptor)

`HttpDigestFields.CreateContentDigestVerifier()` returns a stateless `IHttpExchangeInterceptor` — the
#818 request-parse seam is the natural hook for request-digest validation. It hooks
`AfterRequestBody`:

1. No `Content-Digest`, or only deprecated/unregistered algorithms → pass the body through
   untouched (nothing this library can verify).
2. A malformed `Content-Digest` → **fail closed**: throw `HttpRequestRejectedException(400)`. RFC
   9530 §2 lets a recipient either ignore or reject; an *operator-installed verifier* should not
   silently accept a field it cannot parse. Parsing is CPU-only, so this pre-dispatch rejection
   holds on **every** protocol.
3. Otherwise verify — in one of two modes, chosen by `context.Version`.

### Two verification modes, chosen by where the verdict is affordable

**Eager buffer-and-replay (HTTP/1.1, HTTP/3).** The hook reads the body in full, verifies against
every supported digest with constant-time comparison (`CryptographicOperations.FixedTimeEquals`),
and either rejects a mismatch with `HttpRequestRejectedException(400)` or returns a replay stream
(`HttpDigestReplayStream`) so the application still observes the body. The rejection happens
**before the application runs**, so the transport answers a real, deterministic `400` (h1) or
resets the request stream (h3) with no application involvement. The in-hook full read is free
exactly there: h3 hands the hook a fully received body (drained before header decode), and the h1
parse path is its own body reader — the post-#810 streamed body is pulled from the wire by the
same logical flow that runs the hook, and the peer needs nothing from the server to keep sending
(the `Expect: 100-continue` solicitation is emitted by the body stream's own first read). The cost
is one transient extra copy of the body, bounded by the request-limits cap on h1.

**Lazy verify-on-read (HTTP/2 — and, defensively, any version not proven eager-safe).** On h2 the
parse hooks run on the connection's **single frame pump** and the body is a live
`Http2RequestBodyStream` that may still be arriving under flow-control backpressure (#750): an
in-hook read would wait for DATA frames only the blocked pump can deliver, deadlocking every
multiplexed stream on the connection. So the hook stays CPU-only — it wraps the body in
`HttpDigestVerifyingStream` and reads nothing. The wrapper feeds every octet the application reads
into one BCL `IncrementalHash` per supported digest entry (zero double-buffering; the only copy of
the body is the flow-control-bounded pipe the transport already owns) and resolves the verdict on
the **terminal read** — the read that observes end-of-body:

- **Match** → the terminal read returns `0`; the application saw a normal body end-to-end.
- **Mismatch** → the terminal read throws `HttpContentDigestMismatchException` (naming the first
  failing algorithm in field order), and every subsequent read rethrows it — the failure is
  sticky, so a consumer that swallows it once cannot go on treating the stream as verified.

The mode switch is deliberately an allow-list (`Http11`/`Http30` eager, everything else lazy):
eager is an *optimization* that must be proven safe per protocol, while lazy is safe everywhere
because the hook performs no I/O. An unknown future version therefore degrades to correctness,
not to a deadlock.

### Mid-stream rejection semantics (the lazy path's contract)

Once the application has begun consuming a streamed body, a digest mismatch can no longer become
a clean pre-dispatch `400` — the content was already observed and the exchange is running. The
contract, per the seam's layering rule (interceptors are wiring; aborting is an application-layer
act):

- The typed failure surfaces **on the terminal body read** (`HttpContentDigestMismatchException`,
  `HttpErrorCode.ReadingError`), never earlier — the verdict does not exist until every content
  octet has been hashed.
- The application (or the hosting layer that installed the verifier) must treat the body as
  corrupt, discard anything derived from it, and abort the exchange via `IHttpContext.Cancel`.
  The transport answers the abort with its per-exchange reset — on h2, `RST_STREAM(CANCEL)` from
  the exchange abort path — instead of writing a response. There is deliberately no
  transport-automatic abort: the seam has no abort verb, and the application may prefer to answer
  (e.g. `422`) rather than reset.
- **A body the application never drains is never verified.** Lazy verification can only cover
  what is consumed; a handler that responds without reading to end-of-body has waived
  verification for the unread remainder. Operators who need an unconditional verdict must drain
  the body (h2) or rely on the eager protocols.

Both wrapper streams own the body they replace and dispose it when the exchange disposes the
stream chain, honoring the seam's "a wrapper owns the stream it wraps" contract
(`HttpDigestVerifyingStream` also disposes its running hashes at verdict or disposal, whichever
comes first).

One ordering note applies to both modes: **register the verifier first**, before any
content-decoding interceptor. `Content-Digest` is taken over the message content *as received*
(post-content-coding, on the wire), so it must be verified against the raw body before a
decompression wrapper transforms it.

## Response stamping and Want-* honoring

`HttpDigestFieldsExtensions` adds the response-side surface:

- `IHttpResponse.SetContentDigest(content, algorithm)` / `SetContentDigest(field)` stamp an explicit
  or precomputed digest.
- `IHttpResponse.SetContentDigest(content)` honors the request's `Want-Content-Digest`:
  `HttpWantDigestField.TrySelectPreferred` picks the supported algorithm the client most prefers
  (highest integer preference, ties broken toward the stronger algorithm, `0`/deprecated skipped),
  falling back to `sha-256` when the request expressed no usable preference.
- `IHttpRequest.TryGetContentDigest` / `TryGetWantContentDigest` read the parsed request fields for
  application code that wants them directly.

## Content-Digest vs Repr-Digest scope

`Content-Digest` is implemented end-to-end: header key, value model, computation, and server-side
verification. `Repr-Digest` is **modeled** — the header key exists and `HttpDigestField` parses,
serializes, and computes it identically — but the verifier does not enforce it, because
representation-data reconstruction depends on content-coding and range layers not yet present.
When those land, `Repr-Digest` verification is a drop-in on the same value model.

## AOT posture

No reflection, no runtime codegen. Hashing is BCL `IncrementalHash` over fixed `HashAlgorithmName`
values; comparison is `CryptographicOperations.FixedTimeEquals`; parsing reuses core's span-based
structured-fields parser. The value objects are readonly structs over a single array; the
interceptor allocates only for a verified request (a `Content-Digest` present and verifiable):
one transient body copy plus the replay stream on the eager path, or the verifying wrapper plus
one `IncrementalHash` per supported entry — and **no body copy at all** — on the lazy path.

## Non-goals and honest gaps

- **Repr-Digest enforcement** — modeled, not verified (see above).
- **Trailer emission into a streaming response** — the `HttpContentDigester` primitive and the
  trailer-eligibility guarantee exist; wiring the emitted field into the response trailer section
  rides on the streaming write path (#769).
- **HTTP Message Signatures** — RFC 9530 digests are a building block for signatures covering
  content; the signature layer itself is out of scope.
- **Client-side response-digest verification** — this package's verifier is a server-side request
  surface; the value model and `VerifyContent` are reusable by a future client consumer.
