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
substrate the implementations bind to. The per-database composition below adds
no project or package dependency, including no dependency on Indexing.

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

`TransactionManager.Create(log, lockManager, versionStore, sequenceAllocator?)`
returns the default manager. Lifecycle ordering encodes the write-ahead rule: commit
appends the commit record and awaits durability *while the transaction is still in
the active table* — no snapshot can observe it as committed before its record is on
stable storage; only then does it leave the table and release its locks as a set. A
commit whose record cannot be made durable aborts (versions purged, locks released,
state `Faulted`) and surfaces `TransactionAbortedException`. `OldestActive` is the
pruning bound: `min(active)` or `lastAssigned + 1` when idle. A manager rejects a
context begun on a different manager instance (identity check, not just type check).

**The sequence allocator (why an external hook and not a seed).** An engine that
pairs manager transactions with storage brackets passes the storage's own
allocator (`IStorage.ReserveTransactionSequence`) so both layers share **one
sequence namespace**. The per-database composition uses separately sequenced
physical statement brackets; only the logical transaction's own commit proves
its writer stamp committed. Internally sequenced storage brackets can never
collide with manager assignments. The
alternative — seeding the manager once at open and letting two counters run —
was rejected because any storage-side allocation after the seed (an auto-commit
record bracket) reintroduces collisions. The allocator is invoked *inside* the
manager's begin lock, atomically with active-table insertion, which is what
keeps snapshot capture race-free: another transaction's snapshot either sees
the new sequence in the active set or was taken before it existed. Sequences
the allocator hands to non-manager consumers never stamp row versions, so the
snapshot maximum (`lastSeen + 1`) remains a correct visibility bound even when
it trails the storage counter.

## The lock manager implementation

A lock table keyed by `LockResource` with the classic five-mode compatibility matrix
(S/U/X/IS/IX), same-owner upgrades (an owner's own grants never block it), FIFO
waiter wake-up on `ReleaseAll`, and wait-for-graph deadlock detection: a request that
would close a cycle aborts *itself* with `TransactionDeadlockException` — the
newest-waiter-as-victim policy, chosen because it needs no cost model and the victim
is retryable by construction. Waits are `TaskCompletionSource`-based and honor
cancellation. `ReleaseAll(owner)` ends the owner's participation in the table: it
releases the owner's grants and fails the owner's own queued requests with
`TransactionAbortedException`, because a grant arriving after the owner ended would hold
the resource for a transaction that can never release it (see "Ending a transaction
under a running statement").

## The WAL binding

Storage owns the physical journal (`Database.Storage`); `ITransactionLog` is the *logical* seam: begin/commit/abort records with the write-ahead rule (commit acknowledges only after durability). Group commit is an implementation freedom, not a contract change. This split lets the transaction manager be tested against an in-memory log (`TransactionLog.CreateInMemory()`) while the real one rides the storage journal (`TransactionLog.CreateJournalBound(IStorageJournal)` — commit appends and calls `EnsureDurable`, so concurrent commits naturally share fsyncs). `TransactionRecovery.Analyze(journal)` is the restart-side counterpart: a sequence committed iff its commit record is durable; everything else is aborted and must be purged from version stores.

## Error model

`TransactionAbortedException : Exception` for engine-initiated aborts (an independent exception root — this package is a child root and must not depend on the area contracts; a model engine that surfaces an abort through the area's session contract wraps it in a `DatabaseException` at the model boundary, the same rule the engines apply to `StorageException`); `TransactionDeadlockException : TransactionAbortedException` for deadlock victims (retryable by construction). Caller-initiated rollback is not an error and throws nothing.

## The engine binding (first adopter: the SQL engine)

The area's recorded *isolation split-brain* — a complete MVCC manager no engine
used — closed with the SQL engine's session binding (area DESIGN.md §3.8;
work items under #862). The integration kept this package exactly as shaped:

- The **model engine session** binds the root's `IDatabaseTransaction` to an
  `ITransactionContext` from a per-database `ITransactionManager` — the binding
  lives above both vocabularies (the SQL and KeyValuePair session/transaction adapters),
  per this document's "child root" section; nothing here learned about the area
  contracts. Kernel aborts cross the model boundary wrapped in the root's
  `DatabaseTransactionAbortedException`.
- The root's isolation-level seam plumbs this package's `IsolationLevel` enum
  end-to-end: the manager's per-level semantics (`ReadCommitted` per-access
  refresh, `Snapshot` fixed at begin) are engine behavior now. `Serializable`
  is rejected by the SQL engine until serialization-conflict detection exists —
  the contract forbids running weaker than requested.
- The engine's transaction log is journal-bound to its storage's WAL; the
  sequence allocator (above) unifies the sequence namespace, and per-statement
  storage brackets are the physical WAL brackets beneath the manager (their
  commit records ride the same journal; the manager's commit record owns
  durability through journal ordering). `IStorageTransactionSource` (in
  `Database.Indexing`) is the pairing seam the engine adapts from the shared coordinator —
  resolving a context's current statement bracket. Recovery drives the
  version store's aborted-writer purge from `TransactionRecovery.Analyze` at
  every database open — and `Analyze` reads the active-sequence list out of
  checkpoint records, so classification survives journal truncation beneath
  in-flight transactions.
- The shared `RecordSpaceVersionStore` implements `IVersionStore` over the engine's
  record access adapter (the in-memory store remains for tests and
  embedded working state): row versions live in data pages as stamped records,
  and the store is the *ledger* of each writer's effects, which is what makes
  `PurgeWriterAsync` a physical logical-undo and `PruneAsync` a physical
  space reclamation. `ILockManager` arbitrates row-grain write conflicts
  (exclusive locks on row identity, intent locks at table grain for DDL — the
  B+Tree uniqueness precedent generalized), and deadlock victims cross the
  model boundary as the root's `DatabaseTransactionDeadlockException`.
- The engine's version-purge worker drives the reclamation duties on its own
  timer (#910): `PurgeWriterAsync` retries for aborted writers whose inline
  undo failed, and `PruneAsync` below the safe snapshot bound — the minimum
  snapshot floor across open transactions, not `OldestActive` alone, which
  can trail a live snapshot's view (see the Sql DESIGN.md for the recorded
  bound decision). With that, all four §3.8 steps are implemented.

## Shared per-database composition (#918)

`TransactionCoordinator(storage, journal, records)` owns one manager, lock manager,
record-space version store, gated transaction log, and statement apply semaphore
per database. Pass the journal belonging to that same storage. The caller owns
the storage/journal lifetime and disposes the coordinator before closing them.
SQL and KeyValuePair use this composition directly; there is no engine-specific
copy of its ledger, recovery, prune-bound, or journal-gate mechanics.

The seam follows the executable differences between the original implementations:

- `ITransactionRecordSpace` supplies read/update/delete and physical location
  packing/unpacking. The storage's existing unit iterator scans the stamped
  records. SQL retains its object-id/column tuple and row APIs; KeyValuePair
  retains its key/value tuple and entry APIs. Their current location encoding is
  `(pageId << 16) | (ushort)slotIndex`, but the ledger treats it as an opaque identity.
- `IRecordVersionIndex` supplies stamp-checked erase and clear-deleter operations
  over encoded key bytes. Indexing's `RecordVersionIndex` adapts `IIndex` to this
  new contract. Indexing already references Transactions; the reverse reference
  would form a cycle. The ledger copies keys at registration, keeps index undo
  in the same physical bracket as record undo, and retries the same entries on
  failure. No member was added to any existing interface.
- The engine adapts `TryGetStorageTransaction` to the existing Indexing pairing
  seam, retaining its `DatabaseException` when no statement bracket exists.
  Transactions never references the area root or its exceptions.

`RecordSpaceVersionStore` is a ledger over actual records, not a second payload
store. Logical rollback deletes created versions and clears tombstones only
when their current stamps match the recorded writer. A failed physical statement
can leave stale ledger entries; missing/reverted slots remain harmless through
the original `StorageException`/`ArgumentOutOfRangeException` handling and stamp
rechecks. Committed tombstones enter the prunable set. Pruning requires
`deleter < safeBound` and rechecks the current deleter before deleting. Index
undo stays in recorded order, including its original accounting semantics.

### Ending a transaction under a running statement

A transaction can end on another thread while one of its statements still runs: a session
closing, a caller's rollback racing its own statement, or a host rolling back a wire
session's transaction while a wire command waits. Until the #1225 review the kernel let the
statement go on: `ApplyStatementAsync` opened brackets for any context, and the rollback's
`PurgeWriterAsync` took the writer's ledger before it waited for the apply gate. A bracket
that applied after the undo, or while the undo waited for the gate, stayed stamped with the
rolled-back sequence and in no undone ledger. Every snapshot reads a sequence that is
neither active nor in progress as committed, so readers saw the rolled-back writes; and once
a checkpoint truncated the abort record, recovery classified those stamps as committed and
seeded their tombstones for pruning, so the purge reclaimed the content of records that were
never deleted (a Documents or Blob delete running under a rollback lost committed data
durably).

The kernel now closes a context to statements when its end begins, with three rules:

1. **Admission under the gate.** `ApplyStatementAsync` admits a bracket only after it holds
   the apply gate, and only for a context the manager still holds active whose end has not
   begun (by sequence, so statement wrappers sharing the sequence are covered). Otherwise it
   throws `TransactionAbortedException` and applies nothing.
2. **The end drains the context first.** `CommitAsync`, `RollbackAsync` and the manager's
   internal abort mark the context ending, then wait for the one apply already admitted to
   exit, before the commit record, the undo or the abort record. The wait is bounded: nothing
   awaited inside the gate may actually wait. The undo therefore reads a ledger no bracket can
   still add to, and the commit record follows every bracket stamped with the sequence.
3. **The end fails the owner's queued lock requests.** The manager sets the ended state, then
   calls `ReleaseAll`, which fails the owner's pending requests as well as releasing its grants,
   so a statement parked on a lock fails at once. A request queued just after the release is
   granted later; the engines check the context after every grant they wait for and release a
   grant made to an ended transaction (Graph, Documents and Blob for the database writer lock,
   KeyValuePair for key locks). Setting the state before the release is what makes that check
   sufficient: a grant made after the release always observes the end.

Neo4j terminates a transaction the same way: termination stops the transaction's lock client
(`community/kernel/src/main/java/org/neo4j/kernel/impl/api/KernelTransactionImplementation.java:949-958`),
which marks itself stopped, waits for the lock operations in flight to finish and then releases
all its locks (`community/lock/src/main/java/org/neo4j/kernel/impl/locking/forseti/ForsetiClient.java:578-593`);
a request waiting in the lock loop checks the stopped flag and throws (`ForsetiClient.java:230`,
`1081-1085`), and every later operation of the transaction is refused by `assertOpen`
(`KernelTransactionImplementation.java:1168-1174`). Citations are to Neo4j `54a7dcf7c25`.

Operation-level atomicity stays the engines' job: the kernel guarantees that no bracket lands
after an end begins, not that a multi-bracket operation commits whole. Documents and Blob
refuse a commit while an operation or stream is open, and KeyValuePair refuses one while a
command runs.

### Record stamp prefix: the 16-byte contract

Every record supplied by the adapter starts with this prefix. `RecordVersionStamp`
owns its encoding and the two engines' codecs delegate their stamp operations to it.

| Offset | Size | Encoding | Meaning |
|---|---|---|---|
| 0 | 8 bytes | unsigned 64-bit, little-endian | Writer `TransactionSequence.Value` |
| 8 | 8 bytes | unsigned 64-bit, little-endian | Deleter `TransactionSequence.Value` |
| 16 | remaining bytes | engine-defined | Payload, left untouched by stamp helpers |

Writer zero denotes bootstrap/migrated data visible to every snapshot; deleter
zero means no deletion. A nonzero deleter is a tombstone, not physical removal.
Visibility is exactly `snapshot.IsVisible(writer) && (deleter == None ||
!snapshot.IsVisible(deleter))`. Setting or clearing a deleter produces a copy of
the same length and changes only bytes 8–15, so stamp changes cannot relocate a
record. Short records (less than 16 bytes) are ignored by store visibility,
undo, and recovery scans as before; the low-level helpers require a complete
prefix. Payload interpretation and legacy format upgrades remain engine policy.
B+Tree entries retain their own physical layout and existing stamp implementation.

### Recovery and checkpoint interlock

The ordering is deliberately the same as in both original engines:

1. Open storage with its automatic open-time checkpoint deferred, allowing
   physical WAL recovery without discarding logical transaction classification.
2. Attach the engine's persisted indexes.
3. `AnalyzeAndScrub` analyzes the recovered journal once, removes records created
   by unproven writers, clears their tombstones, and seeds surviving committed
   tombstones for pruning. It then reserves a storage sequence as the recovered
   floor, above every pre-restart stamp.
4. The engine scrubs its attached indexes with that same plan in a durable
   storage bracket.
5. `CompleteRecovery` checkpoints last, before sessions can begin. Truncating
   earlier would erase the lifecycle records needed to classify index entries.

During normal operation, begin/commit/abort appends and changes to the log's
active-sequence set share one monitor with checkpoint capture and truncation.
A begin cannot land between capturing the checkpoint's active list and truncating
the journal. Commit appends and removes its active sequence under that monitor,
then calls `EnsureDurable` outside it; a checkpoint that truncates first has
already durably flushed the outcome. The manager keeps the transaction active
until durability completes. This ordering has not been replaced with an
independent manager-table snapshot.

Statement apply, logical undo, and pruning share one semaphore. Engine conflict
locks are acquired before statement apply, keeping genuine lock waits outside
that semaphore and visible to deadlock detection. Statement brackets commit
non-durably unless the caller selects the existing durable DDL/bootstrap path;
the logical commit makes earlier statement records durable by journal ordering.
Open-time scrub remains ungated because no sessions exist yet.

Logical undo, pruning, and recovery scrub apply at most 64 record/index mutations
per physical bracket. A blob transaction can contain many thousands of chunks;
retaining every touched page's before-image in one undo bracket would otherwise
buffer the entire object. Each batch commits before the next begins. Stamp checks
make retries idempotent even when earlier batches committed before a later batch
failed: the full ledger is requeued, already-undone versions are skipped, and the
remaining work completes. Recovery scrub keeps at most 64 replacement payloads in
memory. The ledger and prune candidates still scale with record count.

Transaction recovery consumes `StorageJournal.ReadSequential` for the shared
journal implementation and falls back to the existing `IStorageJournal.ReadAll`
contract for custom journals. This changes no public interface. Only sequence
classification survives iteration; physical page-image payloads are not retained.

The safe prune bound starts at `max(manager.OldestActive, recoveredSequenceFloor)`
and is reduced to every open context's `Snapshot.Minimum`. A snapshot captured
while an older writer was active can retain a floor below the current oldest
active transaction, so using only the manager's bound would reclaim visible data.
The purge pass retries failed abort undo before pruning, exactly as before.

## Non-goals

- No distributed transactions / two-phase commit — single-node ACID first.
- No lock escalation policy in the contract — an implementation concern.
- No savepoints in the MVP surface — add to `ITransactionContext` when a model needs them.

## AOT posture

Static composition, contracts and value objects; `FrozenSet<ulong>` for the active
set. The shared composition and engine adapters use ordinary calls and BCL
synchronization. No reflection, dynamic code generation, or `Microsoft.Extensions.*`.
