# Assimalign.Cohesion.Database.KeyValuePair.Client — Design

The typed key-value client over the shared `Database.Client` core — the
key-value counterpart of `Sql.Client`, following the same layering: the core
owns pooling, the handshake, framing, and result materialization; this package
owns the typed surface, the model's command/result contract, the error taxonomy,
and telemetry.

## Design intent

- **The client never references the engine package.** The `Sql.Client` precedent
  ("the client never parses — the server owns SQL") holds in mirrored form: this
  client *builds* the model's command grammar (a fixed set of command shapes with
  parameter references — never key or value bytes in the text) and decodes the
  model's fixed result shapes. The shared contract between client and engine is
  the grammar document (`Database.KeyValuePair/docs/COMMANDS.md`) plus the result
  shapes it specifies — a wire contract, not an assembly reference, which is why
  the client types (`KeyValueClientEntry`, `KeyValueScanRange`) are deliberately
  distinct from the engine's model types.
- **Byte-oriented surface.** Keys and values are `ReadOnlyMemory<byte>` end to
  end; they travel as `Binary` tuple-codec components (the shared
  `DatabaseValueCodec` both wire ends speak). Typed value convenience layers
  (string/JSON adapters) are a consumer concern, not client policy.

## The compare-and-swap outcome shape (the recorded decision)

**A conditional miss is a first-class outcome, never an exception.** The
conditional `PutAsync` overload returns `KeyValueWriteResult`
(`Applied` + the new-or-current `ETag`); conditional `TryDeleteAsync` returns
`bool`. Rationale: an etag mismatch means the caller's own view is stale —
ordinary optimistic-concurrency flow on a hot upsert path, where an exception
per miss is an allocation storm and an API lie (nothing failed). What *does*
throw is real contention or failure: the engine's retryable first-updater-wins
write conflict (a concurrently committed change) surfaces as
`KeyValueClientException` with `ExecutionFailure` — deliberately distinct so
retry loops trigger on contention, not on staleness (staleness wants a re-read,
not a blind retry). The rejected alternatives: exceptions for misses (the storm),
and folding conflicts into `Applied=false` (hides contention and breaks the
retry contract).

The unconditional `PutAsync` returns the new etag directly (`long`): an
unconditional upsert always applies, so an outcome wrapper would be ceremony.

## Error surface

`KeyValueClientException : DatabaseException` maps every wire
`ProtocolErrorCode` onto `KeyValueClientErrorKind` (the `SqlClientException`
pattern), preserving the raw code and exposing `ConnectionUsable` so pools and
retry policies can distinguish command-level failures (parse, execution,
malformed result) from broken connections (protocol violation, capacity,
transport). One kind is client-local: `MalformedResult` — the server's reply did
not match the model's result contract (a defense-in-depth check on every decode
path; the connection itself is still healthy because the exchange completed).

## Telemetry

`IKeyValueClientObserver` mirrors `ISqlClientObserver`: synchronous primitive
callbacks around every command (executing / executed / failed), observer
failures swallowed (telemetry must never fault a command), and only the grammar
text is reported — key and value bytes never reach the observer, which keeps
instrumentation allocation-free and leak-safe.

## Materialized scans

`ScanAsync` returns a materialized `IReadOnlyList<KeyValueClientEntry>`: the
shared core materializes wire results while draining the exchange anyway (the
pooling contract — a connection is reusable only once its exchange is fully
consumed), so a streaming surface here would fake laziness over a buffered list.
Bound scans with `KeyValueScanRange.Limit`; incremental paging is cursor
composition on top (scan from the last key), and a genuinely streaming surface
arrives if/when the shared core grows incremental result streaming (its recorded
deferral).

## Non-goals

- Wire transactions — `BEGIN`/`COMMIT` frames are the protocol's documented
  deferral; the client gains a transaction surface when the wire does.
- Typed value serialization, caching, and retry policies — consumer concerns.
- Rent-time liveness pings — the shared core's recorded deferral.

## AOT posture

No reflection, no codegen: fixed command strings, byte parameters, pattern-match
decoding of boxed scalars.
