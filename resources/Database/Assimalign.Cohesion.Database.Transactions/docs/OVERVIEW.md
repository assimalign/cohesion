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

## Dependencies

- `Assimalign.Cohesion.Database.Storage` (child-to-child: the journal/page substrate the implementations bind to)

This package is a **child root** of the Database area: the area root
(`Assimalign.Cohesion.Database`) references it — never the reverse — so the ACID
substrate stays independently consumable.

## Consumers

Every model engine (`Sql`, `Documents`, `Graph`, `Blob`, `KeyValuePair`) composes a transaction manager; execution operators carry `ITransactionContext` into storage, index, and catalog operations.
