# The Key-Value Command Grammar

**Status: wire contract.** The server's internal `ExecuteCommandTextAsync` entry parses
the text and parameters of the protocol's Execute message into typed `KeyValueRequest`
objects. The public `KeyValueDatabaseSession` derives from
`DatabaseExecutionSession<KeyValueRequest>` and accepts typed requests only.

## Why a command grammar

The model has no query language. Its grammar is deliberately not a language: no expressions,
composition or literals except the `LIMIT` count; data operands are named parameters.
The 2026-07-14 decision reused the existing Execute frame and then-shared server pump.
Per-model servers later replaced that pump. The owner decision of 2026-10-16 keeps the
wire encoding and removes the inherited public text capability (#1576).

Update `Internal/Execution/KeyValueCommandParser.cs`, this document, and the command/introspection
corpus tests together whenever the grammar changes.

## Validation and reporting order

1. The server decodes the Execute frame and each tuple-codec parameter. Malformed encoding
   returns fatal `ProtocolViolation` before session checks or grammar parsing.
2. The internal entry checks closed session, blank command, cancellation, offline database,
   then a refusing explicit transaction, in that order.
3. Parse the command and operands into the model's request. Parse failures report event 10,
   `StatementParseFailed`, before returning `ParseFailure`; the connection stays ready.
4. Execute through the public typed NVI, including its validation and tracing. Root statement
   events name the verb request type. Pre-parse refusals and parse failures do not enter this
   NVI and produce no root statement event. Offline refusal returns `Unavailable` and an
   aborted transaction returns `ExecutionFailure`.

Event 10 reports at most once per second with `suppressedCount`. The
`total-statement-parse-failures` counter counts every occurrence, including suppressed
events and failures with no listener. See [DESIGN.md](DESIGN.md) for event payloads.

## Grammar

```
command  :=  get | put | delete | exists | scan | keyspaces

get      :=  GET @key
exists   :=  EXISTS @key
put      :=  PUT @key @value [ IF ABSENT | IF @etag ]
delete   :=  DELETE @key [ IF @etag ]
scan     :=  SCAN [ FROM @start ] [ TO @end ] [ PREFIX @prefix ] [ LIMIT limit ]
keyspaces := KEYSPACES
limit    :=  <non-negative integer literal> | @limit
```

- **Keywords** (`GET`, `PUT`, `DELETE`, `EXISTS`, `SCAN`, `KEYSPACES`, `IF`, `ABSENT`,
  `FROM`, `TO`, `PREFIX`, `LIMIT`) are case-insensitive. Tokens separate on
  whitespace.
- **Operands are parameter references** (`@name`), bound by bare name from the
  execute call's parameter map (the wire's named tuple-codec parameters). The
  only literal the grammar admits is the `LIMIT` count.
- **Clause rules:** `SCAN` clauses may appear in any order, each at most once;
  a non-empty `PREFIX` cannot combine with non-empty `FROM`/`TO` bounds.

## Operand types

| Operand | Required parameter type | Wire encoding |
|---|---|---|
| `@key`, `@value`, `@start`, `@end`, `@prefix` | `byte[]` | `Binary` component |
| `@etag` | `long` (or `int`) | `Int64` component |
| `@limit` | non-negative `int`/`long` | `Int32`/`Int64` component |

A missing parameter, a non-`@` operand token, or a wrong operand type is a
**parse error** (`DatabaseParseException` → `ParseFailure` on the wire) — the
command never reaches execution.

For `SCAN`, a zero-length `@prefix`, `@start` or `@end` byte array means an absent
bound (#1088). Previously, `TO @empty` returned no entries; it now means no upper bound.
Thus `SCAN TO @empty` equals `SCAN`, and `SCAN PREFIX @p FROM @empty`
equals `SCAN PREFIX @p`. A null operand remains a `COHKV012` parse error. An empty
operand still counts as a clause: `FROM @empty FROM @s` is `COHKV010`.
`KeyValueScanOptions`, `KeyValueScanRange` and `KeyValueScanRequest` use the same
zero-length-as-absent rule, including a null `byte[]` implicitly converted to
`ReadOnlyMemory<byte>?`. The request normalizes before validating prefix conflicts;
the typed client omits empty clauses and their parameters from the wire command.

## Diagnostic codes (#1105)

Statement Error text is `CODE: message`. In-process DatabaseParseException.Message
remains code-free and Diagnostics carries the code. These codes are append-only;
all parser refusals use ParseFailure and keep the session ready. COHKV019 is the
ExecutionFailure fallback when the engine supplies no structured error code.

| Code | Refusal |
|---|---|
| `COHKV001` | Empty command |
| `COHKV002` | Attempted catalog mutation |
| `COHKV003` | Unknown command verb |
| `COHKV004` | Request-constructor validation failed |
| `COHKV005` | Invalid PUT arity |
| `COHKV006` | PUT requires IF |
| `COHKV007` | Invalid DELETE arity |
| `COHKV008` | DELETE requires IF |
| `COHKV009` | SCAN clause operand missing |
| `COHKV010` | Repeated SCAN clause |
| `COHKV011` | Unknown SCAN clause |
| `COHKV012` | Byte operand required |
| `COHKV013` | Integer etag required |
| `COHKV014` | Nonnegative LIMIT literal required |
| `COHKV015` | Nonnegative integer LIMIT parameter required |
| `COHKV016` | Parameter reference required |
| `COHKV017` | Parameter value missing |
| `COHKV018` | Command arity mismatch |
| `COHKV019` | Execution failure without an engine diagnostic |

Storage reads fail with structured COHDBK005 for StorageIOException/IOException (SQLSTATE
58030), or COHDBS003 for corruption (XX001). The wire sends ExecutionFailure and keeps the
session. Such a failure aborts an explicit transaction; rollback is required before another
command. Other command errors retain their existing statement-atomic behavior (#1364).

## Semantics and result shapes

| Command | Result | Notes |
|---|---|---|
| `GET @k` | result set `key` (binary), `value` (binary), `etag` (int64) — 0 or 1 rows | absence = zero rows |
| `EXISTS @k` | result set `exists` (boolean) — 1 row | |
| `PUT @k @v` | result set `applied` (boolean), `etag` (int64, nullable) — 1 row; affected count 1/0 | unconditional upsert; `applied` is always true |
| `PUT @k @v IF ABSENT` | same | `applied=false` with the key's current etag when the key exists |
| `PUT @k @v IF @etag` | same | compare-and-swap: `applied=false` with the current etag (null when the key has no visible entry) on a mismatch |
| `DELETE @k [IF @etag]` | plain result; affected count 1/0 | 0 = no visible entry, or the condition missed |
| `SCAN …` | result set `key`, `value`, `etag` — n rows in ascending key order | `FROM` inclusive, `TO` exclusive; `PREFIX p` = `[p, successor(p))` |
| `KEYSPACES` | key-space metadata result set — one row for the implicit space | current database only; columns below |

- **Etag** = the sequence of the transaction that wrote the entry's visible
  version; every applied write produces a new one. A conditional miss is a
  first-class outcome (`applied=false` / affected 0), never an error.
- **Concurrency conflicts are not conditional misses:** a concurrently
  *committed* change to the same key aborts the command's transaction with the
  retryable first-updater-wins conflict (`ExecutionFailure` on the wire; the
  session stays usable).
- Commands execute under the session's current explicit transaction when one is
  active, and auto-commit otherwise — identical to the typed request seam (the
  parser produces the same request objects).

## Key-space discovery (C2)

`KEYSPACES` takes no parameters, clauses, or database selector. It returns the
current database's single implicit key space, including before any entries have
been written. There is no named key-space registry; the identifier is the
catalog registration's object id, currently `1` in every database. It is scoped
by `database_name`, never a server-wide id. The result columns, in order, are:

| Column | Type | Meaning |
|---|---|---|
| `database_name` | string | The receiving session's database |
| `keyspace_id` | int64 | Catalog identity of the implicit key space |
| `entry_space_format_version` | int32 | Catalog's persisted entry-format marker |
| `primary_index_name` | string | Name of the key space's primary index, currently `key` |
| `index_kind` | string | Catalog index kind, currently `BTree` |
| `is_unique` | boolean | Whether the primary index enforces unique keys |

Each command captures the current catalog's format and registrations together.
Its returned rows stay fixed; the next command captures fresh metadata, including
inside an explicit transaction. These metadata publications are self-committing
and are separate from entry MVCC visibility. No metadata entries are stored in
the user key space; index page locations are not exposed. Schema ownership does
not exist in this model, so there are no invented owner/schema columns.

The surface is read-only. `PUT KEYSPACES ...` and `DELETE KEYSPACES ...` fail
with `DatabaseParseException` (`ParseFailure` over the wire) and the stable
wire text `COHKV002: The KEYSPACES catalog surface is read-only.` A parameter-bound key
whose bytes spell `KEYSPACES` remains ordinary user data. The typed equivalent
is `KeyValueKeySpacesRequest`, executed through the existing session interface.

## Non-goals

- No `BEGIN`/`COMMIT`/`ROLLBACK` verbs — explicit transaction control over the
  wire is the protocol's documented deferral (transaction frames), not a model
  grammar concern.
- No multi-key commands (batch/atomic multi-put) — a future grammar revision,
  gated on the model roadmap.
- No database-management verbs — the wire carries none by area principle
  (code-first provisioning, DESIGN §2.4).
