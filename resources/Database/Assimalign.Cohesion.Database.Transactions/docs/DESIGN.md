# Assimalign.Cohesion.Database.Transactions — Design

## Intent

One transaction substrate for five engines. ACID is the platform's defining requirement (area `DESIGN.md` R1), so the machinery that provides it — MVCC snapshots, commit ordering, locking, WAL binding — lives in a kernel project no model owns. Engines *use* the manager; sessions *expose* the resulting `IDatabaseTransaction` from the contract root.

## Why MVCC (and not lock-based isolation)

Readers never block writers and writers never block readers — the OLTP profile all five models share. Locking is retained only where MVCC cannot arbitrate: write-write conflicts, in `ILockManager`, with intent modes so object-level operations (drop table, reindex) coexist with entry-level writes.

## Identity vs. ordering: `TransactionId` vs. `TransactionSequence`

`TransactionId` (contract root) is a GUID — a good *external* identity for sessions, diagnostics, and the wire protocol, but unordered. Visibility decisions need a total order, so this project introduces `TransactionSequence`, a monotonically increasing `ulong` assigned at begin. The split mirrors PostgreSQL's virtual-txid vs. xid distinction and keeps the public contract root free of MVCC mechanics.

## Snapshot semantics

`TransactionSnapshot` captures `(owner, minimum, maximum, active-set)` and answers `IsVisible(writer)`:

- own writes → visible;
- `writer >= maximum` (began after capture) → invisible;
- `writer < minimum` (decided before the oldest in-flight) → visible;
- otherwise → visible iff the writer was not in the active set.

Note the deliberate simplification: a version whose writer *aborted* below `minimum` must never be consulted, so the version store — not the snapshot — is responsible for unlinking aborted versions during rollback/recovery. That keeps the snapshot a pure value object with no commit-log lookup.

`ReadCommitted` refreshes the snapshot per statement; `Snapshot` (the default) and `Serializable` fix it at begin. `Serializable` layers conflict detection on top and is a post-MVP feature — the enum member exists so the surface doesn't churn.

## The WAL binding

Storage owns the physical journal (`Database.Storage`); `ITransactionLog` is the *logical* seam: begin/commit/abort records with the write-ahead rule (commit acknowledges only after durability). Group commit is an implementation freedom, not a contract change. This split lets the transaction manager be tested against an in-memory log while the real one rides `IJournalLogger`.

## Error model

`TransactionAbortedException : DatabaseException` for engine-initiated aborts; `TransactionDeadlockException : TransactionAbortedException` for deadlock victims (retryable by construction). Caller-initiated rollback is not an error and throws nothing.

## Non-goals

- No distributed transactions / two-phase commit — single-node ACID first.
- No lock escalation policy in the contract — an implementation concern.
- No savepoints in the MVP surface — add to `ITransactionContext` when a model needs them.

## AOT posture

Pure contracts and value objects; `FrozenSet<ulong>` for the active set. No reflection, no codegen.
