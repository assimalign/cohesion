# The Key-Value Command Grammar

**Status: contract.** This grammar is the key-value model's statement surface —
the text the session's text-execute seam parses
(`IDatabaseSession.ExecuteAsync(string, parameters)`), and therefore exactly what
rides the wire protocol's Execute message. Like the SQL dialect's `DIALECT.md`,
changes here change a contract: update the parser
(`Internal/KeyValueCommandParser.cs`), this document, and the corpus tests
(`tests/KeyValueCommandTests.cs` and `tests/KeyValueIntrospectionTests.cs`) together, always.

## Why a command grammar (the text-seam decision, 2026-07-14)

The key-value model has **no query language** (the area's recorded layout
verdict) — but the area's wire protocol carries statements as *text plus named
tuple-codec parameters* (`Execute`), and the root's session contract carries the
matching text-execute seam. Giving the model a small command grammar makes it
**wire-compatible with the existing Execute message and the generic server
session pump with zero protocol changes** — the alternative (model-specific
binary command frames) would have forked the protocol and the server machinery
for no expressiveness gain. The grammar is deliberately not a language: no
expressions, no literals except the `LIMIT` count, no composition — every data
operand is a named parameter.

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
  `PREFIX` cannot combine with `FROM`/`TO`.

## Operand types

| Operand | Required parameter type | Wire encoding |
|---|---|---|
| `@key`, `@value`, `@start`, `@end`, `@prefix` | `byte[]` | `Binary` component |
| `@etag` | `long` (or `int`) | `Int64` component |
| `@limit` | non-negative `int`/`long` | `Int32`/`Int64` component |

A missing parameter, a non-`@` operand token, or a wrong operand type is a
**parse error** (`DatabaseParseException` → `ParseFailure` on the wire) — the
command never reaches execution.

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
message `The KEYSPACES catalog surface is read-only.` A parameter-bound key
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
