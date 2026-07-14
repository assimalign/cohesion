# Assimalign.Cohesion.Database.Transactions — Design

## Intent

One transaction substrate for five engines. ACID is the platform's defining requirement (area `DESIGN.md` R1), so the machinery that provides it — MVCC snapshots, commit ordering, locking, WAL binding — lives in a kernel project no model owns. Engines *use* the manager; sessions *expose* the resulting `IDatabaseTransaction` from the contract root.

## Why MVCC (and not lock-based isolation)

Readers never block writers and writers never block readers — the OLTP profile all five models share. Locking is retained only where MVCC cannot arbitrate: write-write conflicts, in `ILockManager`, with intent modes so object-level operations (drop table, reindex) coexist with entry-level writes.

## A child root — no area dependency

This package is a child root the area root aggregates (root → Transactions,
never the reverse — the 2026-07-13 inversion; see the area DESIGN.md decision
log). The transaction vocabulary lives here — `TransactionId`,
`TransactionState`, `TransactionSequence`, `IsolationLevel` — and the root's
`IDatabaseTransaction` contract consumes it through the root's child-root
reference. The contracts in this package speak only Transactions-owned types:
the earlier doc-level nods to the root's `IDatabaseTransaction` are *named*
(`<c>`), not referenced (`<see cref>`), and the adaptation between
`ITransactionContext` and the public `IDatabaseTransaction` surface belongs to
whoever owns both vocabularies — the model engines' session/transaction
implementations above the root (the same place `IQueryTransactionScope` in
`Execution` puts its engine adaptation, the area's standing cycle-avoidance
shape). `Storage` is the one reference (child-to-child): the journal/page
substrate the implementations bind to.

## Identity vs. ordering: `TransactionId` vs. `TransactionSequence`

`TransactionId` is a GUID — a good *external* identity for sessions, diagnostics, and the wire protocol, but unordered. Visibility decisions need a total order, so this project also has `TransactionSequence`, a monotonically increasing `ulong` assigned at begin. The split mirrors PostgreSQL's virtual-txid vs. xid distinction and keeps the public session surface free of MVCC mechanics.

## Snapshot semantics

`TransactionSnapshot` captures `(owner, minimum, maximum, active-set)` and answers `IsVisible(writer)`:

- own writes → visible;
- `writer >= maximum` (began after capture) → invisible;
- `writer < minimum` (decided before the oldest in-flight) → visible;
- otherwise → visible iff the writer was not in the active set.

Note the deliberate simplification: a version whose writer *aborted* below `minimum` must never be consulted, so the version store — not the snapshot — is responsible for unlinking aborted versions during rollback/recovery. That keeps the snapshot a pure value object with no commit-log lookup. This duty is a contract member: `IVersionStore.PurgeWriterAsync(writer)`, called by the manager on rollback/abort and by recovery for every sequence the journal cannot prove committed.

`ReadCommitted` refreshes the snapshot per statement; `Snapshot` (the default) and `Serializable` fix it at begin. The refresh mechanism: the context's `Snapshot` property re-captures from the manager's live active table on every access while the transaction is active — each statement reads it once. `Serializable` layers conflict detection on top and is a post-MVP feature — the enum member exists so the surface doesn't churn.

## The manager implementation

`TransactionManager.Create(log, lockManager, versionStore)` returns the default
manager. Lifecycle ordering encodes the write-ahead rule: commit appends the commit
record and awaits durability *while the transaction is still in the active table* —
no snapshot can observe it as committed before its record is on stable storage; only
then does it leave the table and release its locks as a set. A commit whose record
cannot be made durable aborts (versions purged, locks released, state `Faulted`) and
surfaces `TransactionAbortedException`. `OldestActive` is the pruning bound:
`min(active)` or `lastAssigned + 1` when idle.

## The lock manager implementation

A lock table keyed by `LockResource` with the classic five-mode compatibility matrix
(S/U/X/IS/IX), same-owner upgrades (an owner's own grants never block it), FIFO
waiter wake-up on `ReleaseAll`, and wait-for-graph deadlock detection: a request that
would close a cycle aborts *itself* with `TransactionDeadlockException` — the
newest-waiter-as-victim policy, chosen because it needs no cost model and the victim
is retryable by construction. Waits are `TaskCompletionSource`-based and honor
cancellation.

## The WAL binding

Storage owns the physical journal (`Database.Storage`); `ITransactionLog` is the *logical* seam: begin/commit/abort records with the write-ahead rule (commit acknowledges only after durability). Group commit is an implementation freedom, not a contract change. This split lets the transaction manager be tested against an in-memory log (`TransactionLog.CreateInMemory()`) while the real one rides the storage journal (`TransactionLog.CreateJournalBound(IStorageJournal)` — commit appends and calls `EnsureDurable`, so concurrent commits naturally share fsyncs). `TransactionRecovery.Analyze(journal)` is the restart-side counterpart: a sequence committed iff its commit record is durable; everything else is aborted and must be purged from version stores.

## Error model

`TransactionAbortedException : Exception` for engine-initiated aborts (an independent exception root — this package is a child root and must not depend on the area contracts; a model engine that surfaces an abort through the area's session contract wraps it in a `DatabaseException` at the model boundary, the same rule the engines apply to `StorageException`); `TransactionDeadlockException : TransactionAbortedException` for deadlock victims (retryable by construction). Caller-initiated rollback is not an error and throws nothing.

## Next iteration — binding engine sessions to the manager (scoped, not yet true)

Today **no engine uses this manager**: the SQL engine isolates through
`Database.Storage`'s page-grain single-writer transactions, and this package's
snapshots/locks/version stores run only under their own tests — the area's
recorded *isolation split-brain* (design: `resources/Database/DESIGN.md` §3.8;
work items under #862). The integration keeps this package exactly as shaped:

- The **model engine session** binds the root's `IDatabaseTransaction` to an
  `ITransactionContext` from an engine-level `ITransactionManager` — the
  binding lives above both vocabularies, per this document's "child root"
  section; nothing here learns about the area contracts.
- The root's isolation-level seam (`IDatabaseSession.BeginTransactionAsync(IsolationLevel, …)`,
  landed 2026-07-13) plumbs this package's `IsolationLevel` enum end-to-end;
  the manager's existing per-level semantics (`ReadCommitted` per-access
  refresh, `Snapshot` fixed at begin) become engine behavior at binding time.
- Engines adopt `IVersionStore` for row versions, `ILockManager` for row-grain
  write conflicts (hashed-key exclusive locks — the B+Tree uniqueness
  precedent), and activate `PurgeWriterAsync` + the `OldestActive` prune bound
  from their version-purge workers. Recovery drives `PurgeWriterAsync` from
  `TransactionRecovery.Analyze` for unproven sequences — the contract member
  exists for exactly this.
- Storage transactions remain the physical WAL bracket beneath the manager;
  `IStorageTransactionSource` (in `Database.Indexing`) is the pairing seam.

## Non-goals

- No distributed transactions / two-phase commit — single-node ACID first.
- No lock escalation policy in the contract — an implementation concern.
- No savepoints in the MVP surface — add to `ITransactionContext` when a model needs them.

## AOT posture

Pure contracts and value objects; `FrozenSet<ulong>` for the active set. No reflection, no codegen.
