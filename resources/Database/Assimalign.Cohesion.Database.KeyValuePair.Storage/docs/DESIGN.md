# Assimalign.Cohesion.Database.KeyValuePair.Storage — Design

## Design intent

The thinnest possible model-storage satellite: it types a storage file set as
`StorageModel.KeyValue` and exposes the kernel's protected record surface under
entry-flavored names (`InsertEntry`/`ReadEntry`/`UpdateEntry`/`DeleteEntry`). The
key-value model deliberately brings **no model-specific physical layout** — the
generality bet of the second model is that the shared substrate (slotted pages,
per-owner record chains, WAL v2, recovery) serves an ordered key-value store
as-is, and this project is where that bet is visibly small.

## Why-this-not-that

- **A `Storage` subclass, not a wrapper.** `SqlStorage` set the family shape: the
  kernel's record operations are `protected`, and the model satellite is the
  sanctioned way to expose the subset a model composes. A wrapper would duplicate
  the surface; a public kernel record API would invite models to bypass their
  engines.
- **No entry codec here.** The record byte layout (16-byte version-stamp header +
  key/value tuple) is engine policy, tied to the engine's MVCC composition — it
  lives with the version store and executor in `Database.KeyValuePair`, exactly
  where `SqlRowCodec` lives in `Database.Sql`. This satellite stays byte-agnostic.
- **Owner-chain inserts are the primary insert path.** The engine keys every entry
  record by its key-space object id so the key space owns its pages (the #911
  per-object chain design). The shared-space overload exists for the catalog file
  set, whose records are few and unowned.

## Lifecycle

Instances are created/opened by the engine's storage strategy and disposed by the
engine's database instance. `Open(..., checkpointOnOpen: false)` is the reopen
contract: the engine runs `TransactionRecovery.Analyze` over the recovered journal
before any truncation (classification reads lifecycle records a checkpoint would
destroy) and checkpoints through its coordinator once analysis completes.

## Non-goals

- Model-specific page layouts (adjacency pages, blob streams) — the key-value
  model needs none.
- Public exposure of the kernel's full record surface — only what the engine
  composes is exposed, and `WriteAheadJournal` stays internal to the family
  (`InternalsVisibleTo` the engine package, the `SqlStorage` precedent).

## AOT posture

No reflection, no codegen — inherited span-based record operations only.
