# Assimalign.Cohesion.Database.Transactions — Overview

The shared ACID substrate for every Cohesion database engine: MVCC visibility snapshots, isolation levels, hierarchical locking for write-write conflict control, and the transaction-log seam that binds commits to the write-ahead log.

## Scope

- `ITransactionManager` — begin/commit/rollback lifecycle, sequence assignment, snapshot capture
- `ITransactionContext` — the engine-internal state of one in-flight transaction
- `TransactionId` / `TransactionState` — the transaction vocabulary the area root's `IDatabaseTransaction` contract consumes
- `TransactionSnapshot` / `TransactionSequence` — MVCC visibility (implemented, tested)
- `ILockManager` / `LockMode` / `LockResource` — hierarchical write locking with deadlock resolution
- `ITransactionLog` — the WAL binding (storage owns the physical journal)
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

A commit whose record was appended but whose durable flush failed is committed in memory and
reported as `TransactionCommitUnconfirmedException`; the failed flush took the storage offline
(#1243), so nothing more is written until the database is reopened, and the reopen's recovery
decides whether the commit survived. The coordinator's `Checkpoint` takes the statement apply
gate, so a size-triggered checkpoint cannot be starved by a sustained write load (#1254).

## Dependencies

- `Assimalign.Cohesion.Database.Storage` (child-to-child: the journal/page substrate the implementations bind to)

This package is a **child root** of the Database area: the area root
(`Assimalign.Cohesion.Database`) references it — never the reverse — so the ACID
substrate stays independently consumable.

## Consumers

Every model engine (`Sql`, `Documents`, `Graph`, `Blob`, `KeyValuePair`) composes a transaction manager; execution operators carry `ITransactionContext` into storage, index, and catalog operations.
