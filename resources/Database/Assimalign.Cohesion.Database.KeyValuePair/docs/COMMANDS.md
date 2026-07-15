# The Key-Value Command Grammar

**Status: contract.** This grammar is the key-value model's statement surface —
the text the session's text-execute seam parses
(`IDatabaseSession.ExecuteAsync(string, parameters)`), and therefore exactly what
rides the wire protocol's Execute message. Like the SQL dialect's `DIALECT.md`,
changes here change a contract: update the parser
(`Internal/KeyValueCommandParser.cs`), this document, and the corpus tests
(`tests/KeyValueCommandTests.cs`) together, always.

## Why a command grammar (the text-seam decision, 2026-07-14)

The key-value model has **no query language** (the area's recorded layout
verdict) — but the area's wire protocol carries statements as *text plus named
tuple-codec parameters* (`Execute`), and the root's session contract carries the
matching text-execute seam. Giving the model a five-verb command grammar makes it
**wire-compatible with the existing Execute message and the generic server
session pump with zero protocol changes** — the alternative (model-specific
binary command frames) would have forked the protocol and the server machinery
for no expressiveness gain. The grammar is deliberately not a language: no
expressions, no literals except the `LIMIT` count, no composition — every data
operand is a named parameter.

## Grammar

```
command  :=  get | put | delete | exists | scan

get      :=  GET @key
exists   :=  EXISTS @key
put      :=  PUT @key @value [ IF ABSENT | IF @etag ]
delete   :=  DELETE @key [ IF @etag ]
scan     :=  SCAN [ FROM @start ] [ TO @end ] [ PREFIX @prefix ] [ LIMIT limit ]
limit    :=  <non-negative integer literal> | @limit
```

- **Keywords** (`GET`, `PUT`, `DELETE`, `EXISTS`, `SCAN`, `IF`, `ABSENT`,
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

## Non-goals

- No `BEGIN`/`COMMIT`/`ROLLBACK` verbs — explicit transaction control over the
  wire is the protocol's documented deferral (transaction frames), not a model
  grammar concern.
- No multi-key commands (batch/atomic multi-put) — a future grammar revision,
  gated on the model roadmap.
- No database-management verbs — the wire carries none by area principle
  (code-first provisioning, DESIGN §2.4).
