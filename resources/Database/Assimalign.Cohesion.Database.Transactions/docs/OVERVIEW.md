# Assimalign.Cohesion.Database.Transactions — Overview

The shared ACID substrate for every Cohesion database engine: MVCC visibility snapshots, isolation levels, hierarchical locking for write-write conflict control, and the transaction-log seam that binds commits to the write-ahead log.

## Scope

- `TransactionManager` — begin/commit/rollback lifecycle, sequence assignment, snapshot capture (a sealed type; the coordinator composes the durable one, `TransactionManager.Create` a standalone one over an in-memory log)
- `TransactionContext` — the engine-internal state of one in-flight transaction (sealed;
  `PinStatementSnapshot` fixes a read-committed statement's snapshot)
- `TransactionSavepoint` — the sealed, opaque token for the coordinator's non-virtual undo controls
- `TransactionId` / `TransactionState` — the transaction vocabulary the area root's `DatabaseTransaction` base consumes
- `TransactionSnapshot` / `TransactionSequence` — MVCC visibility (implemented, tested)
- `LockManager` / `LockMode` / `LockResource` — hierarchical write locking with deadlock resolution
  (sealed; `new LockManager()` for a standalone table); `LockStatementMark` preserves earlier
  grants while unwritten upserts and cascades release new locks and retry preparation
- the transaction log — the WAL binding, an internal seam since #1257 (storage owns the physical journal)
- `VersionStore` — the version-chain base; its two leaves, `RecordSpaceVersionStore` and the
  in-memory store (`VersionStore.CreateInMemory`), both live here
- `TransactionRecordSpace` / `RecordVersionIndex` — the abstract adapters an engine supplies
  for its stamped records and its index undo, which the coordinator drives
- `TransactionAbortedException` — an independent exception root (inherits `Exception`, not `DatabaseException`)

The coordinator's internal anonymous markers capture positions in the ordered row/index
ledger. Partial undo reverses a marker's suffix through bounded physical brackets while the
root transaction stays open. Marker-scoped grants are released and upgrades revert with undo
(#1726, XT-3). Owned tokens expose these controls to model adapters; root sessions supply names,
duplicate lookup, RELEASE scope and repeatable ROLLBACK TO in SQL/OQL/GQL (XT-4). Incomplete
undo fences work until retry to the same or an outer scope, or whole rollback. Markers stay in
memory and storage stays at format 4. See DESIGN for failure recovery and cancellation.

The coordinator's record ledger supports streamed model records: logical rollback,
pruning, and recovery scrub use bounded physical mutation batches, while lifecycle
analysis streams the shared journal instead of retaining page-image payloads.

A transaction's end closes it to statements: once a commit, rollback or abort claims
the end, the coordinator applies no further bracket of it, the end waits for the bracket
already applying, and the transaction's queued lock requests fail
(`LockManager.ReleaseAll`). A statement still running when its transaction ends on
another thread therefore fails with `TransactionAbortedException` and leaves nothing
stamped with the ended sequence. A started rollback always ends its transaction, even
when its abort record or its undo fails (#1226); a writer whose undo failed keeps its
granted locks, and stays in flight for every snapshot, until a retry completes the undo.
The retry runs on its own backoff — 100 ms after the deferral, doubling up to the engine's
maintenance interval (`DeferredUndoRetryDelay`, `DeferredUndoRetryLimit`, `OnUndoDeferred`,
`RetryDeferredUndo`) — so a transient failure releases the writer within about a second.

A commit whose record was appended but whose drain or durable flush failed is committed in memory
and reported as `TransactionCommitUnconfirmedException`; the failed drain or flush took the storage
offline (#1243, #1252), so nothing more is written until the database is reopened, and the reopen's recovery
decides whether the commit survived. An offline database releases no lock until the reopen, so
the engines wire their storage's offline hook to `AbandonLockWaits`, which fails every lock wait
then in progress, and every later one, with the storage's offline error (#1268 review). The
coordinator's checkpoint runs under the statement
apply gate, so a size-triggered checkpoint cannot be starved by a sustained write load (#1254).
`TryCheckpoint` never waits for a statement: when one holds the gate, it runs the checkpoint as
it ends, so an engine's checkpoint worker is not held up by one database's long statement. A
checkpoint asked for inside a statement apply is refused instead of waiting forever.

## Dependencies

- `Assimalign.Cohesion.Database.Storage` (child-to-child: the journal/page substrate the implementations bind to)

This package is a **child root** of the Database area: the area root
(`Assimalign.Cohesion.Database`) references it — never the reverse — so the ACID
substrate stays independently consumable.

## Consumers

Every model engine (`Sql`, `Documents`, `Graph`, `Blob`, `KeyValuePair`) composes a transaction manager; execution operators carry `TransactionContext` into storage, index, and catalog operations.

The statement-mark/re-grant zero-allocation contract is verified in optimized Release tests
with tiered compilation disabled. Functional mark and arbitration tests also run in Debug.
See DESIGN, unwritten statement marks, for the platform-measurement regression after #1689.

Exact allocation guards also run with background GC disabled in this test host: an upstream
runtime accounting defect can report unused context bytes as allocations during an empty
interval. A runtime-configuration guard and a real-allocation positive control verify the
measurement. Product GC policy and runner parallelism are unaffected. DESIGN records the
controlled reproduction found during #1288 and its relation to the statement-mark guard.

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

## Shared index purge (#1195)

The coordinator overload accepting `Func<TransactionSequence, CancellationToken, ValueTask<long>>`
runs a bounded index batch under the apply gate after record pruning. Its public `PruneBound`
also supplies safe opportunistic unique-insert pruning. SQL, KeyValuePair, Documents and Graph
bind their current managers; the existing purge count/event includes reclaimed index entries.
The original constructor and physical statement rollback remain supported.


## Confirmed decode failures (#1372)

Storage's D1 seqlock confirms private record copies before models decode them. Malformed
copies report the existing corruption code with page and slot. Direct catalog APIs retain
their model exception type and cause; engine boundaries use the common corruption exception. The shared version store rejects records shorter than its
16-byte stamp header and retains failed undo/prune work for retry. StorageCorruptionException
adds a record-scoped constructor and nullable SlotIndex. Formats and wire frames are unchanged.

## Soft deadlocks (#1420)

Detected cycles first try consistent resource-queue reordering. The repair preserves actual
holder conflicts and original waiter cancellation/timeout ownership; a hard held-lock cycle
still chooses a victim. Ordinary queues retain FIFO/holder placement. The SQL cascade uses the
existing statement marks to release new grants before a wait, retain the waited grant and
restart its unapplied closure. No key-share mode or public locking signature is added.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.
`LockManager()` is public; the parameterless `Create()` factory is removed. FIFO grant order, statement marks and deadlock handling are unchanged.

## Debugger inspection convention

Transaction debugger views filter private/internal coordination state. Reviewed public identity, sequence, isolation and state values may be projected; snapshot capture, prune-bound work, oldest-active scans, lock-taking counts and retry-clock/provider calls are omitted. The views do not enter a transaction, acquire a lock, refresh a snapshot or expose internal-only counters.

Concrete eligible types carry their own concise display and private nested DebugView at the end of the type; an effective inherited display/proxy pair is reused. Projections are public read-only members of that private view and never reveal private/internal data. Empty views use the constant type name. Debugger attributes and nested infrastructure add no public API, instance fields to existing runtime types, storage/wire format change or successful-path work.
