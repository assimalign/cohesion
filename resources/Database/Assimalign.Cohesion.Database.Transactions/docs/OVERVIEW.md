# Assimalign.Cohesion.Database.Transactions — Overview

The shared ACID substrate for every Cohesion database engine: MVCC visibility snapshots, isolation levels, hierarchical locking for write-write conflict control, and the transaction-log seam that binds commits to the write-ahead log.

## Scope

- `TransactionManager` — begin/commit/rollback lifecycle, sequence assignment, snapshot capture (a sealed type; the coordinator composes the durable one, `TransactionManager.Create` a standalone one over an in-memory log)
- `ITransactionContext` — the engine-internal state of one in-flight transaction
- `TransactionId` / `TransactionState` — the transaction vocabulary the area root's `IDatabaseTransaction` contract consumes
- `TransactionSnapshot` / `TransactionSequence` — MVCC visibility (implemented, tested)
- `ILockManager` / `LockMode` / `LockResource` — hierarchical write locking with deadlock resolution
- the transaction log — the WAL binding, an internal seam since #1257 (storage owns the physical journal)
- `IVersionStore` — the version-chain contract model storage layers implement
- `TransactionAbortedException` — an independent exception root (inherits `Exception`, not `DatabaseException`)

The coordinator's record ledger supports streamed model records: logical rollback,
pruning, and recovery scrub use bounded physical mutation batches, while lifecycle
analysis streams the shared journal instead of retaining page-image payloads.

A transaction's end closes it to statements: once a commit, rollback or abort claims
the end, the coordinator applies no further bracket of it, the end waits for the bracket
already applying, and the transaction's queued lock requests fail
(`ILockManager.ReleaseAll`). A statement still running when its transaction ends on
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

Every model engine (`Sql`, `Documents`, `Graph`, `Blob`, `KeyValuePair`) composes a transaction manager; execution operators carry `ITransactionContext` into storage, index, and catalog operations.
