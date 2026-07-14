# Assimalign.Cohesion.Database.KeyValuePair — Design

The key-value engine (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md) §3.3, generality report §3.10):
an ordered key space over the shared kernel, and the **second model engine** —
built deliberately as the proof that the kernel is model-general, not SQL-shaped.

## Design intent

Compose kernel pieces, never re-implement them — and compose them in a
*different shape* than the SQL engine, because the difference is the point:

| | SQL engine | Key-value engine |
|---|---|---|
| Primary structure | the record space (scan-primary; indexes are secondary accelerators) | **the B+Tree primary key index** (index-primary; every read is a seek) |
| Record payload | object-id-prefixed typed tuple, schema from the catalog | key + value as two binary tuple components, self-describing |
| Conflict grain | table intent locks + per-row location locks + unique-key locks | **key locks only** (one per command) |
| Statement surface | the SQL dialect | five command verbs (docs/COMMANDS.md) |
| Catalog | schemas/tables/columns/indexes | registrations + format marker only |

Both engines share, unchanged: the storage substrate (slotted pages, per-owner
chains, WAL v2, recovery), the MVCC discipline (16-byte writer/deleter stamp
prefix, snapshot visibility, first-updater-wins latest-state checks, logical
rollback via the version-store ledger, open-time recovery scrub), the B+Tree
(MVCC-stamped entries, latest-state uniqueness under hashed-key locks), the
one-sequence-namespace pairing, and the per-statement bracket/apply-gate model.

## Execution model

- **Index-primary reads.** `GET`/`EXISTS` seek the unique primary index
  (`key` → packed record location) through the command's snapshot; `SCAN` drives
  a snapshot cursor over an `IndexKeyRange`. Keys go into `IndexKey` **raw** —
  the key-value ordering contract (unsigned lexicographic byte comparison) *is*
  `IndexKey`'s comparison, so no codec transformation applies to keys. Every
  fetched record's stamps are re-checked against the same snapshot (the SQL seek
  executor's defense-in-depth discipline: entries mirror record stamps by
  construction, so a divergence is a bug this filter contains). Prefix scans map
  to `[prefix, successor(prefix))` by byte-successor arithmetic; an all-0xFF
  prefix is unbounded above.
- **Entry records.** `[writer u64][deleter u64]` — the fixed 16-byte stamp
  header, the same layout the SQL record space and the B+Tree leaves carry —
  followed by the shared tuple codec payload (`key` binary component, `value`
  binary component). Entries live in the key space's per-object page chain
  (owner id 1), so a full scan of the database touches only entry pages. The
  key is stored in the record (not only in the index) so recovery scrubs and
  integrity checks are self-describing; format version 1, catalog-persisted,
  rejected-if-newer at open (no upgrade machinery — the model was born stamped).
- **Writes are two-phase, key-grain.** Phase one: acquire the key's Exclusive
  lock (`LockResource.Entry(keySpace, IndexKey.Hash())` — the same identity the
  B+Tree's unique enforcement locks internally, so its in-gate re-acquisition is
  a same-owner re-grant), resolve the visible version by index seek, re-validate
  its **current** stamps under the lock (a foreign committed deleter =
  first-updater-wins conflict, retryable), and decide conditional writes. Phase
  two: the coordinator's gated apply bracket — tombstone the old version in
  place (same-length stamp write), insert the new version into the chain, mirror
  both in the primary index, ledger every effect. A unique violation on the
  insert is a concurrently committed invisible writer → translated to the same
  retryable conflict.
  - **Why no row/location locks (a deliberate divergence from SQL):** every
    key-value mutation is keyed by exactly one key, and every version of a key
    is only ever mutated by that key's writer — so the key lock subsumes
    per-location locks entirely. Key-grain locks are the model's whole
    user-visible conflict surface; deadlocks (multi-key transactions) surface
    through the shared lock manager's requester-closes-cycle detection.
- **Etags = writer sequences.** An entry's etag is the `TransactionSequence`
  that wrote its visible version — R1's "key/etag uniqueness" for free: the
  sequence namespace is unique per database, every applied write produces a new
  etag, and the stamp is already in the record. Surfaced as `long` (the wire's
  `Int64` component).
  - **Compare-and-swap is a conditional decision, not a conflict** (the
    recorded outcome-shape decision): `IF @etag` / `IF ABSENT` misses return
    first-class not-applied outcomes (`applied=false` + current etag, affected
    count 0) with **no mutation and no exception** — an etag mismatch means the
    caller's *own* view is stale, which is application flow, not contention.
    Contention (a concurrently *committed* change racing the command) instead
    aborts with the root's retryable `DatabaseTransactionAbortedException` —
    same taxonomy as SQL. Rejected alternative: throwing on CAS misses (an
    exception storm on a hot upsert path) or folding conflicts into
    `applied=false` (hides real contention and breaks retry semantics).
- **Transactions.** Identical binding to the SQL engine's (§3.8): per-database
  `KeyValueTransactionCoordinator` (manager + lock manager + record-space
  version store + gated journal-bound log, one sequence namespace with storage),
  explicit transactions and auto-commit both ride manager contexts, `Snapshot`
  default / `ReadCommitted` per-command refresh / `Serializable` rejected,
  rollback is logical through the ledger, recovery classifies + scrubs record
  space and primary index at open. Kernel aborts are wrapped in the root's
  exceptions at the session boundary (the area error policy).
- **Result shapes.** `GET`/`SCAN` return result sets (`key`, `value`, `etag`);
  `PUT` a one-row outcome set (`applied`, `etag`); `EXISTS` a one-row boolean
  set; `DELETE` a plain result with its affected count. These shapes ride the
  wire's generic ResultHeader/Row/Complete framing untouched — a deliberate
  constraint so the model needs no protocol surface of its own.

## The text seam (docs/COMMANDS.md — the grammar contract)

The session's text-execute seam parses the five-verb command grammar into the
same typed requests the typed seam executes. **Decision (2026-07-14): the
recommended minimal-grammar shape was taken** — it makes the model
wire-compatible with the existing `Execute` message (statement text + named
tuple-codec parameters) and the generic server session pump with **zero protocol
changes**, which is also what made the server-core extraction evidence
conclusive (area DESIGN §3.10). The rejected alternative — model-specific binary
command frames ("KV wants binary command paths", the extraction trigger's
prediction) — would have forked the protocol message family and the server pump
for no expressiveness gain over named binary parameters; it remains open as a
measured-need optimization, not a default. The grammar is a contract: parser,
COMMANDS.md, and the corpus tests change together (the DIALECT.md precedent).

## The key-value server runtime (`KeyValueDatabaseServer`)

The model ships its own wire-protocol server — the **second model server**, the
one whose construction fired the area's recorded server-core extraction trigger
(2026-07-14). `KeyValueDatabaseServer` is a sealed derivation of the shared
server core's guided base (`Assimalign.Cohesion.Database.Server`'s
`DatabaseServer`), fronting exactly one `KeyValueDatabaseEngine`
(`Create(engine, options)`, options in
`KeyValueDatabaseServerOptions : DatabaseServerOptions`). It adds **no pump
behavior**: the command grammar travels the protocol's existing Execute message
into the root's text-execute seam, and the model's result sets ride the generic
result framing — which is precisely the evidence that made the extraction's
scope larger than predicted (the shared core's docs/DESIGN.md carries the
prediction-vs-evidence record). Model-specific wire surface (binary command
frames, if measurement ever demands them) would grow here.

## Engine-owned background workers

The same five-worker inventory as the SQL engine, spawned at creation on
engine-owned threads, quiesced on dispose (engines are data machines; R10):
group-commit WAL flusher (signal-driven), paced page write-back, checkpointer
(both file sets; data set through the coordinator so truncating checkpoint
records carry in-flight sequences; re-exports index registrations when drifted),
**version purge — live** (the KV MVCC binding is real from the first cut, so the
purge duty is real: aborted-undo retries + reclamation below the minimum
snapshot floor), and the index-maintenance **stub** (the index layer has no
compaction yet — the stub matters more here than in SQL, since every delete
accrues a tombstone in the primary structure; the seam is kept stable for the
compaction feature). Cadence knobs live on `KeyValueDatabaseEngineOptions`.

## Two file sets per database

`<name>` (entries + primary index pages — index pages ride the data storage's
transactional page surface, no separate index file) and `<name>.catalog`
(registrations + format marker), both via `IKeyValueStorageStrategy` —
file-backed under `RootPath`, in-memory otherwise, the SQL strategy pattern.
The primary index bootstraps at database creation inside a durably-committed
bracket (the self-committing DDL posture), then persists its registration and
the format marker as catalog self-commits; a crash between tree build and
registration leaves only an orphaned root page (safe leak), repaired by
re-bootstrapping on the next open.

## Error model

`DatabaseException` (area root) for misuse and model errors;
`DatabaseParseException` for grammar violations (→ `ParseFailure` on the wire);
`DatabaseTransactionAbortedException`/`DatabaseTransactionDeadlockException`
(retryable) for MVCC conflicts — kernel exceptions are translated at the model
boundary, never leaked raw.

## Known duplication (recorded kernel gaps — see area DESIGN §3.10)

`KeyValueTransactionCoordinator`, `KeyValueVersionStore`, and the stamp half of
`KeyValueRecordCodec` are near-verbatim adaptations of their SQL counterparts:
the per-database MVCC composition proved model-agnostic in mechanics but has no
kernel home yet, so the second model paid a copy. Extracting it is filed work —
the generality report row carries the item numbers. The copies are deliberate
(hacking a premature kernel package into shape mid-bring-up would have risked
the SQL engine's stability for a refactor the third model can validate instead).

## Non-goals (current cut)

- **TTL/expiration** — the area model table lists TTL for this model; it is
  deferred (filed work), and the surface was deliberately shipped without
  `ExpiresAt` so etag semantics landed clean first.
- Named key spaces (multiple ordered key spaces per database) — the catalog
  reserves the concept; the engine currently owns one implicit key space.
- Multi-key atomic batches, `Serializable` isolation, secondary value indexes,
  index compaction (the stub worker's future body).

## AOT posture

No reflection, no runtime codegen: byte spans, the shared tuple codec, and
boxed scalars only at the result-row boundary (the Execution family's shape).
