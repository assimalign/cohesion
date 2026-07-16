# Database.Sql.Client — Design

## Design intent

`Database.Sql.Client` is the **typed relational surface** over the shared client
core (`Database.Client`). It exists so a SQL consumer writes commands, binds typed
parameters, and reads typed rows — the ADO.NET vocabulary — while every hard problem
(dialing, the handshake, connection pooling and session reuse, frame codecs, result
materialization) stays solved once in the model-agnostic core beneath it. The package
is deliberately thin: it is a projection and an error/telemetry adapter, not a second
protocol implementation.

## Why-this-not-that

### Layer over `Database.Client`, don't re-implement it
The core already rents authenticated connections, streams and materializes results,
and maps wire errors. This package wraps `IDatabaseConnection` in an `ISqlConnection`
and projects the core's `DatabaseClientResult` (boxed values) into a typed
`SqlResultSet`. Re-implementing pooling or framing here would duplicate the core's
DoS-critical logic and drift from it — the same reason the area keeps one wire
protocol and one server (`resources/Database/DESIGN.md`).

### No `Sql.Language` dependency — the client never parses SQL
The server owns parsing: it receives statement text + decoded parameters and each
model's session translates that into its typed request (the root text-execute seam,
`IDatabaseSession.ExecuteAsync(string, parameters)`). A client that parsed SQL would
duplicate the dialect and couple to the engine's internal AST/plan structures, which
the #180 design note explicitly warns against ("keep client contracts stable and
separate from internal plan structures"). So the client sends text and reads rows;
it references only `Database`, `Database.Client`, `Database.Types`, and `Connections`.

### A SQL-scoped exception, not the raw wire code
The core throws `DatabaseClientException` carrying a `ProtocolErrorCode`. Surfacing
raw wire codes to SQL callers would leak a transport concern and force every consumer
to learn the protocol enum. Instead `SqlClientException` maps each code onto a
`SqlClientErrorKind` (parse / execution / connection / auth / unavailable / …) and
still preserves the underlying `Code` for diagnostics. `ConnectionUsable` encodes the
core's own rule — statement-level failures (parse, execution, transaction abort) keep
the session ready, everything else breaks it — so callers can retry on the same
connection without re-deriving that from codes.

### Widening typed getters via `Convert.ChangeType`, not strict casts
Row values arrive boxed to their exact runtime type from the shared value codec (an
`Int32` column is a boxed `int`). A strict `(long)` unbox of a boxed `int` throws, so
`SqlRow` returns the value directly on an exact type match and otherwise widens
through `Convert.ChangeType(…, InvariantCulture)` — the IConvertible fast path, which
is AOT-safe (no reflection). This makes `GetInt64` on an `INT` column, or a `BIGINT`
read as `long`, "just work" while a genuinely incompatible read fails as an
`InvalidCast` `SqlClientException`. Culture is pinned to invariant so numeric/text
coercions never depend on the ambient locale.

### Parameter names normalize the sigil away
Parameters bind by **bare name** on the wire (the dialect's `@`/`$` sigil is
presentation only, per the SQL engine's bind rule). `SqlParameterCollection` strips a
leading `@`/`$` on add, so `Add("@id", …)` and `Add("id", …)` both bind the `@id`
placeholder — the caller doesn't have to know which form the wire wants.

### A primitive telemetry hook, not an event/DI pipeline
`ISqlClientObserver` takes primitives (`string`, counts, `TimeSpan`) rather than event
objects, so instrumenting a client allocates nothing per command and stays trimming-
clean. It is a plain options property — no DI, honoring the area rule that only
`*.Hosting` is a DI seam. Observer callbacks are invoked defensively (a throwing
observer is swallowed, never faulting or masking the command) because telemetry must
not change execution outcomes.

## Family position

```
Database.Client  (model-agnostic core: dial, handshake, pool, materialize)
        ▲
        │ layers over
        │
Database.Sql.Client  (typed commands, result sets, errors, telemetry)
```

Other per-model clients (`Documents.Client`, `KeyValuePair.Client`, …) follow the
same shape over the same core; none depend on this package.

## Lifecycle

- `ISqlClient` owns the underlying `IDatabaseClient`; disposing the SQL client
  disposes the core client, closing every pooled connection.
- `ISqlConnection` wraps one rented `IDatabaseConnection`; disposing it returns the
  connection to the pool (with its authenticated session intact when healthy) — it is
  not a real socket close.
- Connections are single-exchange and not thread-safe, mirroring the engine-session
  contract.

## Error model

`SqlClientException : DatabaseException` (area root). Carries `Kind`
(`SqlClientErrorKind`) and `Code` (`ProtocolErrorCode`). Built from a core
`DatabaseClientException` via `FromClientException`, or directly for client-local
failures (`InvalidCast` on a bad typed read). No new area exception root — it inherits
the database area's `DatabaseException`.

## AOT posture

No reflection, no runtime codegen. Typed conversion uses the IConvertible fast path
(`Convert.ChangeType`), which is trimming-safe. Value projection is hand-written. The
telemetry surface is allocation-free at the call site.

## Non-goals

- **No SQL parsing / plan awareness.** The client is text-in, rows-out.
- **No incremental (`IAsyncEnumerable`) streaming yet.** Results materialize fully,
  matching the core's MVP; a streaming surface arrives with the model clients that
  need it.
- **No connection-string driver selection.** Transports are composed statically
  (`IConnectionFactory`), never named in the string — an AOT requirement inherited
  from the core.
- **No transactions surface yet.** Explicit transaction control rides a later
  protocol/schema addition; the MVP is auto-commit per statement.
