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
implementations above the root (the area's standing cycle-avoidance shape). `Storage`
is the one reference (child-to-child): the journal/page
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

`TransactionManager` is a public sealed type with an internal constructor (#1257, the
concrete-first rule in `database-area.md`): there is one manager implementation, so the
`ITransactionManager` interface and the `DefaultTransactionManager` behind it collapsed
into it. It stays public, not internal, because `TransactionCoordinator.Manager` exposes
it and foreign test assemblies (Indexing.Tests, Sql.Tests) read it. Two factories build
one: the public `TransactionManager.Create(lockManager, versionStore, sequenceAllocator?)`
returns a standalone manager over an in-memory log, and an internal overload that also
takes the log serves the coordinator's composition and this package's own tests. The
durable manager of a database is the one `TransactionCoordinator` composes over its
storage journal. Lifecycle ordering encodes the write-ahead rule: commit
appends the commit record and awaits durability *while the transaction is still in
the active table* — no snapshot can observe it as committed before its record is on
stable storage; only then does it leave the table and release its locks as a set. A
commit whose record cannot be written aborts, ending the transaction the way a
rollback does (state `Faulted`; see "Ending a transaction" below), and surfaces
`TransactionAbortedException`. A commit whose record was written but whose durable
flush failed is different, and is described next. `OldestActive` is the
pruning bound: `min(active)` or `lastAssigned + 1` when idle. A manager rejects a
context begun on a different manager instance (identity check, not just type check).

### A commit record that was written but not made durable

The log's `AppendCommitAsync` appends the commit record and then flushes it. When the
append succeeds and only the flush fails, the journal-bound logs throw
`TransactionCommitUnconfirmedException`, and the manager ends the transaction as
**committed**: it leaves the active table, its state becomes `Committed`, its locks are
released, and the exception reaches the caller. Any other failure of `AppendCommitAsync`
still means the record was not written, and the transaction aborts as above.

A written commit record cannot be taken back. Recovery classifies a writer as committed
whenever its commit record is in the journal, and the gated log drops the writer from the
checkpoint's active list as soon as the record is appended, so a later checkpoint presumes
it committed. Until the #1226 integration review the manager treated the flush failure as an
abort and undid the writer. That was false whenever the record reached stable storage
anyway: after a crash the writer came back committed, and when the undo was deferred
("Ending a transaction", below) its versions stayed in the record space beside a commit
record, so even a clean close brought back a transaction its caller had been told was
aborted. The outcome is now the one a crash decides: committed if the record survives,
nothing at all if it does not. No later transaction can be durable without it, because its
own commit record follows this one in the journal and a flush covers everything before the
record it flushes.

**The failed flush takes the database offline (#1243).** On a WAL flush that fails,
PostgreSQL stops the server (`issue_xlog_fsync` raises `PANIC`,
`src/backend/access/transam/xlog.c:9877-9937`; `RecordTransactionCommit` flushes the commit
record inside a critical section, so any failure there is `PANIC` too,
`src/backend/access/transam/xact.c:1470-1583`), because a retried fsync can report success for
bytes the operating system already dropped. The storage does the equivalent without stopping
the process: the failing flush latches the journal and the storage offline
(`StorageOfflineException`, `COHDBS002`; `Database.Storage` DESIGN.md, "A failed durable flush
takes the storage offline"), and the unconfirmed commit carries that exception as its inner
exception (`StorageOfflineException.Find` locates it). From then on nothing is written to the
database's files — no append, no checkpoint, no undo, no write-back, not even at close — and
every engine refuses every operation on it, in process and over its wire server, with
`DatabaseOfflineException` and the engine's own code, until the database is reopened. The
reopen runs recovery over the journal as the media holds it, which keeps the commit if its
record's bytes survived and undoes it if they did not: the caller's "unconfirmed" is decided
exactly as a crash at that moment would decide it.

So no other transaction reads the writer's effects before the outcome is known: the database
refuses every statement once the flush failed, and the coordinator's close skips the undo of
the transactions still active (`DisposeAsync` treats a refused abort on an offline storage as
expected; the reopen's recovery aborts and scrubs them). Before #1243 the kernel kept running
after the failure, other transactions could read the writer's effects, and the next commit's
flush could succeed over records the failed one had lost. The area root carries the outcome as
`DatabaseTransactionCommitUnconfirmedException`, which every engine translates the kernel
exception to, and which is not retryable.

## Ending a transaction: a started rollback always completes (#1226)

A rollback, once it starts, ends its transaction whatever fails or is canceled. The
start is a claim: `CommitAsync`, `RollbackAsync` and the abort that disposal forces
each claim the context's end first, so exactly one of them runs. A second attempt
while one runs fails with `TransactionAbortedException` ("already ending"), and a
commit can never race a rollback of the same writer. The same claim closes the
context to statement applies ("Ending a transaction under a running statement",
below): the context carries one end flag, so the end that wins the claim is also the
end every later apply is refused for. `RollbackAsync` observes the caller's token only
before the claim: a token canceled by then throws `OperationCanceledException` and
leaves the transaction active and untouched. After the claim no token reaches the
rollback, and it throws nothing:

1. **Drain.** The rollback waits for the statement apply already admitted for the
   context, if one is running. The wait observes no token and is bounded (nothing
   awaited inside the apply gate may actually wait), so it cannot stop a started
   rollback; a commit drains the same way before its commit record.
2. **Undo.** `IVersionStore.PurgeWriterAsync(writer)` removes the writer's versions.
3. **State.** The state becomes `RolledBack`, or `Faulted` for an abort a failed commit
   or disposal forced.
4. **Abort record.** The transaction log's `AppendAbortAsync(writer)`. A failure is ignored.
5. **Release.** The writer leaves the active table, then its locks are released, which
   also fails any lock request of it still queued.

**The abort record is advisory.** Recovery classifies by commit records alone:
`TransactionRecovery.Analyze` puts every sequence it meets (in a begin, page image or
rollback record, or in a checkpoint's active list) into `Aborted` unless the journal
holds its commit record (`TransactionRecovery.cs:87-88`). A writer whose abort record
was lost therefore reads as aborted after a crash, exactly like one whose record was
written, and the open-time scrub of an already-undone writer changes nothing because
it is stamp-checked. PostgreSQL relies on the same presumption: `RecordTransactionAbort`
does not flush its abort record "since the default assumption after a crash would be
that we aborted, anyway" (`src/backend/access/transam/xact.c:1825-1827`), loss of an
abort record "is noncritical; the presumption would be that it aborted, anyway"
(`xact.c:1456-1458`), and marking the transaction aborted in clog is "not absolutely
necessary" because "in event of a crash we'd be assumed to have aborted anyway"
(`xact.c:1885-1890`; PostgreSQL commit `85f55534e80`). The coordinator's journal-bound
log drops the writer from the checkpoint's active list even when the append fails:
the manager appends the record only after the undo completed, so nothing of the
writer is left for recovery to scrub.

That presumption covers an abort record that never reached the log, not a write that
failed part way. PostgreSQL treats any failed WAL write as fatal (`ereport(PANIC,
"could not write to log file ...")`, `src/backend/access/transam/xlog.c:2529-2532`),
because bytes a failed write left behind would end recovery's read of the log before
every later record. This journal gives the same guarantee without stopping the
process. Since #1252 an append writes nothing: records wait in the journal's append buffer
until it drains. The abort record's append itself fails only when the journal is already
offline, or when it has to drain a full buffer and that write fails; otherwise the record is
lost, if at all, with the later drain that carries it (the next commit's, a checkpoint's),
after the rollback returned. Either way the failed write takes the storage offline, so nothing
is written behind the bytes it left (`Database.Storage` DESIGN.md, "The append buffer"). The
rollback still ends; the reopen's recovery reads the writer as aborted. The engines' rollback
tests lose the record that second way, failing the drain that carries it
(`FailJournalWritesContaining`). Ignoring a failed
abort record therefore never hides a later commit record from recovery. (Until #1252 a
failed append cut its partial frame back off and the storage stayed online.)

**The token stops at the start.** A rollback stopped half way would keep the locks of
a writer whose work it only partly undid, the zombie this rule removes. PostgreSQL
holds interrupts through the whole of `AbortTransaction` ("Prevent cancel/die
interrupt while cleaning up", `xact.c:2860`). Graph's transaction already followed the
rule (#1188); the SQL, KeyValuePair, Documents and Blob transactions now observe the
caller's token before the start and pass none to the coordinator.

**When the undo itself fails.** This is the one failure a rollback cannot absorb by
ignoring it. The writer's versions are still in the record space, and the snapshot has
no commit-log lookup ("Snapshot semantics", above), so the writer must keep reading
as in flight. The transaction still ends: the caller's rollback returns and the state
is `RolledBack`. But the writer stays in the active table, keeps its granted locks, and
gets no abort record, so checkpoints keep carrying it in their active list. Its queued
lock requests fail at once, exactly as the release at any other end fails them: the
transaction has ended, so a statement of it parked on a lock must not wait for a grant
it could only give back (the default lock manager's internal `AbandonPending` fails the
queue without releasing a grant). A request it makes after that point is refused at once
too, unless it re-requests a lock it already holds as strongly: queued, it would join the
wait-for graph, where a live transaction could be chosen as the deadlock victim of one that
has already ended, and granted, it would be held until the undo completes. Its end claim
keeps refusing its statement applies even though the active table still holds it. The manager owns
this deferred undo. The coordinator retries it — `RetryDeferredUndo` when the retry schedule
below says one is due, and every `RunVersionPurgePass` — and when the undo completes the manager
appends the abort record, removes the writer from the active table and releases its locks. Holding the locks until the undo completes follows
PostgreSQL, which holds regular locks "till we finish aborting" (`xact.c:2873`). Neo4j
releases them in a `finally` whether or not its rollback threw
(`community/kernel/.../KernelTransactionImplementation.java:1216-1229`, `1614-1623`;
commit `54a7dcf7c25`), but its rollback only discards in-memory transaction state and
leaves nothing on disk to undo; this kernel's undo is physical. Releasing them first
would let the next lock holder build on versions about to be undone, and the Graph,
Documents and Blob engines' latest-state checks, which build a snapshot from the open
contexts under the database writer lock, rely on no lock holder leaving effects
unresolved. A manager composed directly through `TransactionManager.Create` has no
version-purge pass, so its deferred undo is retried only at disposal, and the writer's
locks are held until then (the factory's remarks say so).

The cost of that rule is an availability one, and it is a deliberate departure from the
literal wording of #1226's first acceptance criterion ("always releases the context's
locks"): while an undo keeps failing, every writer that conflicts with the rolled-back
one waits. Graph, Documents and Blob take one database writer lock, so there that is every
writer. The alternative, releasing first, trades the wait for reading rolled-back writes as
committed, which no engine may do.

**The retry runs on its own backoff (#1226, owner decision of 2026-10-04).** Until then the
only retry was the version-purge pass, one `MaintenanceInterval` (60 seconds by default) after
the failure, so even a transient failure — one journal write refused, before #1252 — held every
conflicting writer for a minute. Now the manager schedules the retry itself (`DeferredUndoBackoff`): the
first is due `DeferredUndoRetryDelay` (100 ms) after the deferral, each retry that leaves a
writer deferred doubles the delay up to `DeferredUndoRetryLimit` (the engine's maintenance
interval), a new deferral starts the schedule over, and the schedule stops once nothing is
deferred. The coordinator invokes `OnUndoDeferred` when an undo is deferred, which wakes the
engine's version-purge worker; the worker sleeps no longer than `NextDeferredUndoRetry` and calls
`RetryDeferredUndo`, which does nothing until a retry is due. A transient failure therefore
releases the writer about 100 ms later: every engine's storage-operations test makes one undo
fail with an hour-long maintenance interval and has the next writer proceed within 1% of it (in
practice a few hundred milliseconds). The failure is a page write lock another storage bracket
holds while the rollback runs (the shared test object `PageWriteLockHolder`); until #1252 it was a
failed undo journal write, which now takes the storage offline instead, so a journal fault is no
longer one a retry can outlive. A failure that persists is retried at 0.1, 0.2,
0.4 … seconds, then once per maintenance interval, so it cannot spin. The purge workers record a
retry that fails again with anything other than a busy page or bracket as a worker fault and
keep running; before, the exception escaped the worker's pump loop and stopped the worker for
good, leaving every deferred writer stuck until the database closed. The purge workers report
such a failure for its database with no backoff of their own
(`DatabaseEngineWorker.ReportFailure(name, exception, TimeSpan.Zero)`, #1268 review), so this
schedule alone paces the retries, and one database's failing undo delays no other database's
retry. (For a while #1268 slept the whole worker a second after any failed pass, which floored
every database's retries at a second.) A busy page or bracket (`StorageTransactionException`,
which the page write lock above raises) is no failure: the workers retry it on the next pass and
report it only as unfinished work (`ReportUnfinished`), which keeps an earlier failure of the
database recorded but never records one, so an undo that a held page keeps failing is retried on
the schedule and never surfaces as a fault. The schedule reads a `TimeProvider` (the
coordinator's internal constructor), so `DeferredUndoBackoffTests` and the coordinator's tests
drive it without waiting.

**An offline storage ends every lock wait (#1268 review).** An offline database undoes nothing:
a transaction that was writing when its storage went offline keeps its locks until the reopen,
whose recovery aborts it, for the same reason a deferred undo keeps them (releasing them first
would let the next holder build on versions that were never undone). A request queued behind
such a transaction would wait for a release that never comes, and in every engine only the
reopen ended it (reproduced with an explicit transaction holding the lock in all five engines).
`TransactionCoordinator.AbandonLockWaits(cause)`, which each engine wires to its data storage's
`OnOffline` hook, fails every wait in progress and every later one with
`TransactionAbortedException` whose inner exception is the storage's offline error, which the
engines translate into their coded offline refusal. Every way a storage goes offline raises that
hook: a failed durable flush (#1243), a failed drain of the journal's append buffer (#1252,
`StorageOfflineCause.JournalFlush` like the fsync) and a failed header slot write (#1268); the
coordinator's tests end the waits on both journal failures. A request the lock table can grant
at once is still granted: the coordinator's lock view tries the grant first, so an uncontended request
costs nothing more, and only a request that has to wait links its token with the abandonment.
The waits are failed asynchronously (`CancellationTokenSource.CancelAsync`), because the hook
may run under the storage's locks. Neo4j's lock client ends the waits of a stopped client the
same way (`community/lock/src/main/java/org/neo4j/kernel/impl/locking/forseti/ForsetiClient.java:1081-1085`).

Any failure inside the undo is an undo failure like any other. The undo's
storage bracket fails to begin, to touch a page or to commit, rolls itself back, and
the writer is deferred. A storage bracket ends even when its own begin or rollback
record cannot be appended, and a page whose full page image cannot be appended is left
unlocked (`Database.Storage` DESIGN.md, "Failed appends"), so a failed undo leaves
nothing behind in the storage: checkpoints keep running while the writer waits, and
the retry can touch the same pages. A failure that is a journal write takes the storage
offline since #1252 ("The append buffer" there): the retry is then refused like every other
write, and the reopen's recovery scrubs the writer.

Each of those checkpoints truncates the journal, the writer's begin record with it, while
the writer's versions stay in the data pages, so the writer's classification at the next
open rests on the checkpoint's own list of writers. The journal's copy of that
list is appended after the truncation, and an append that fails there, or a crash between
the truncation and the record's flush, would leave the journal empty and the writer
unnamed: recovery would then read its versions as committed (#1226 integration review). The
storage therefore also writes the list into its file header before the truncation, and makes
that header generation durable first (`Database.Storage` DESIGN.md, "Checkpoints").
`AnalyzeAndScrub` adds that anchor to the journal's classification, so the writer is aborted
and scrubbed at the next open whether the checkpoint record survived or not. The same holds
for any writer still in flight at a checkpoint, deferred or not.

**Only the manager releases a managed writer's locks.** The manager releases a
transaction's locks as a set at the moment the transaction leaves its active table,
which covers any grant the transaction received after it ended. The lock manager the
coordinator hands to engine code (`TransactionCoordinator.LockManager`) therefore
ignores `ReleaseAll` for a transaction the manager still tracks. Engine code calls it
when an operation of a transaction that ended while the operation waited receives its
grant late (the Graph, Documents and Blob writer-lock helpers, the graph store, and the
KeyValuePair key-lock acquisition do; "Ending a transaction under a running statement",
rule 3). Before that rule, such a call released a deferred writer's database writer
lock before its undo ran, and the next writer's latest-state snapshot, built from the
open contexts, read the rolled-back versions as committed. For a transaction the
manager no longer tracks, the call releases as before. The two rules compose: the
manager sets the ended state before it removes the writer from the active table, and
removes it before it releases, so an engine's post-grant check always sees the end,
and its `ReleaseAll` either lands while the manager still tracks the writer (the
manager's release, which follows, covers the late grant) or after it (the call
releases the late grant itself). The view decides only who releases, not whether an
ended owner's queued requests fail: the manager fails them itself, through the lock
manager underneath, at its release or, for a deferred end, at the end. A `ReleaseAll` the
view absorbs neither releases a grant nor fails a request; the engines call it only after
their own request was granted, so they have nothing queued for it to fail.

**Closing with an undo that still fails.** Disposal aborts every transaction still
active, waits for every commit or rollback already running (so storage never closes
under one), retries every deferred undo once more, and rethrows an undo that still
fails after everything else is done. That writer's versions survive the close, and only
the next open's recovery can remove them, which it does only if the journal still
classifies the writer at that open. A storage's clean close would not keep it so: an
idle storage closes with a checkpoint that truncates the journal and lists no active
transaction, which erases the writer's begin record and every checkpoint entry carrying
it. So the coordinator, before it rethrows, begins one storage bracket per such writer
under the writer's own sequence (`Storage.BeginTransaction(long)`, which writes
nothing). The writer is then in flight at the physical layer as well, the storage's
close takes its non-idle path (flush pages and journal, no truncation), and the next
open finds the writer without a commit record, classifies it as aborted and scrubs it,
exactly as after a crash. When an earlier checkpoint already truncated the writer's begin
record and lost its own record, the journal no longer names the writer at all; the
storage's checkpoint anchor still does, and the non-idle close leaves the anchor as that
checkpoint wrote it. Every engine closes its storage whether or not the
coordinator's close threw: Graph, Documents and Blob already did; SQL and KeyValuePair
now do too, where they used to leave their storages open.

**The sequence allocator (why an external hook and not a seed).** An engine that
pairs manager transactions with storage brackets passes the storage's own
allocator (`Storage.ReserveTransactionSequence`) so both layers share **one
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
under a running statement"). The default lock manager also has an internal
`AbandonPending(owner)`, the first half alone: it fails the owner's queued requests and
keeps its grants, for the end of a rollback whose undo is deferred ("Ending a
transaction"). Until `ReleaseAll` the owner stays abandoned: `AcquireAsync` refuses its new
requests with `TransactionAbortedException` and `TryAcquire` returns false, except for a
resource it already holds in a mode at least as strong, which changes nothing for anyone
else. No public member was added for it; a lock manager of another type passed
to `TransactionManager.Create` leaves such a writer's queued requests queued until its
deferred undo completes.

## The WAL binding

Storage owns the physical journal (`Database.Storage`); the transaction log is the *logical* seam: begin/commit/abort records with the write-ahead rule (commit acknowledges only after durability). Group commit is an implementation freedom, not a contract change. This split lets the transaction manager be tested against an in-memory log (`TransactionLog.CreateInMemory()`) while the real one rides the storage journal (`TransactionLog.CreateJournalBound(StorageJournal)` — commit appends and calls `EnsureDurable`, so concurrent commits naturally share fsyncs). `TransactionRecovery.Analyze(journal)` is the restart-side counterpart: a sequence committed iff its commit record is durable; everything else is aborted and must be purged from version stores.

**The log is internal (#1257).** `TransactionLog` is an `internal abstract` class in `Internal/`, and its two factories are internal statics on it: every variant (the in-memory log, the journal-bound log, the coordinator's gated journal log) lives in this assembly, and the only other derivations are this package's own fault-injecting test doubles, which reach it through the assembly's `InternalsVisibleTo` grant. The public `ITransactionLog` interface and the `public static class TransactionLog` factory are gone; only tests ever called the factories.

## Error model

`TransactionAbortedException : Exception` for engine-initiated aborts (an independent exception root — this package is a child root and must not depend on the area contracts; a model engine that surfaces an abort through the area's session contract wraps it in a `DatabaseException` at the model boundary, the same rule the engines apply to `StorageException`); `TransactionDeadlockException : TransactionAbortedException` for deadlock victims (retryable by construction). `TransactionCommitUnconfirmedException : Exception`, a second independent root, for a commit whose record was written but not made durable: the transaction is committed, so it is deliberately not an abort and not retryable ("A commit record that was written but not made durable", above); engines wrap it in the area's `DatabaseTransactionCommitUnconfirmedException`. Caller-initiated rollback is not an error: once started it throws nothing (#1226); before the start it throws only for a canceled token or a context it cannot end, and `ObjectDisposedException` once the manager's disposal began.

## The engine binding (first adopter: the SQL engine)

The area's recorded *isolation split-brain* — a complete MVCC manager no engine
used — closed with the SQL engine's session binding (area DESIGN.md §3.8;
work items under #862). The integration kept this package exactly as shaped:

- The **model engine session** binds the root's `IDatabaseTransaction` to an
  `ITransactionContext` from a per-database `TransactionManager` — the binding
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
  timer (#910): it retries the undo of rolled-back writers whose inline undo
  failed, releasing each writer once its undo completes (#1226, "Ending a
  transaction" above), and `PruneAsync` below the safe snapshot bound — the minimum
  snapshot floor across open transactions, not `OldestActive` alone, which
  can trail a live snapshot's view (see the Sql DESIGN.md for the recorded
  bound decision). With that, all four §3.8 steps are implemented.

## Shared per-database composition (#918)

`TransactionCoordinator(storage, journal, records)` owns one manager, lock manager,
record-space version store, gated transaction log, and statement apply semaphore
per database. Pass the journal belonging to that same storage. The caller owns
the storage/journal lifetime and disposes the coordinator before closing them,
and closes them even when the coordinator's disposal throws ("Closing with an
undo that still fails", above).
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
2. **The end drains the context first.** `CommitAsync`, `RollbackAsync` and the abort a
   failed commit or disposal forces claim the context's end (the #1226 claim, "Ending a
   transaction", above), which closes its apply admission, then wait for the one apply
   already admitted to exit, before the commit record, the undo or the abort record. The
   wait is bounded: nothing awaited inside the gate may actually wait. The undo therefore
   reads a ledger no bracket can still add to, and the commit record follows every bracket
   stamped with the sequence. A rolled-back writer whose undo is deferred stays in the
   active table, but its claim keeps refusing its applies.
3. **The end fails the owner's queued lock requests.** The manager sets the ended state, then
   calls `ReleaseAll`, which fails the owner's pending requests as well as releasing its grants,
   so a statement parked on a lock fails at once. A rollback whose undo is deferred fails the
   pending requests the same way but keeps the grants until the undo completes. A request
   queued just after that point is granted later; the engines check the context after every
   grant they wait for and give back a grant made to an ended transaction (Graph, Documents
   and Blob for the database writer lock, KeyValuePair for key locks), through the
   coordinator's lock-manager view, which leaves the release to the manager while the manager
   still tracks the transaction. Setting the state before the release is what makes that check
   sufficient: a grant made after the release always observes the end.

Neo4j terminates a transaction the same way: termination stops the transaction's lock client
(`community/kernel/src/main/java/org/neo4j/kernel/impl/api/KernelTransactionImplementation.java:949-958`),
which marks itself stopped, waits for the lock operations in flight to finish and then releases
all its locks (`community/lock/src/main/java/org/neo4j/kernel/impl/locking/forseti/ForsetiClient.java:578-593`);
a request waiting in the lock loop checks the stopped flag and throws (`ForsetiClient.java:230`,
`1081-1085`), and every later operation of the transaction is refused by `assertOpen`
(`KernelTransactionImplementation.java:1168-1174`). Citations are to Neo4j `54a7dcf7c25`.

A statement that fails keeps its own error. Its bracket rolls back before the failure
propagates, and when only the bracket's rollback record cannot be appended (the bracket still
ends, `Database.Storage` DESIGN.md, "Failed appends"), that advisory record's failure is
dropped rather than thrown in place of the statement's. The engines name the statement's
failure as the cause of an aborted transaction (`COHDBD001`, `COHDBB001`, `COHDBG007`), so a
journal error must not take its place. A failure that leaves the bracket active (restoring
its pages failed) still propagates.

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
3. `AnalyzeAndScrub` analyzes the recovered journal once, together with the storage's
   checkpoint anchor (`Storage.CheckpointActiveTransactions`, the writer list the last
   checkpoint wrote into the file header before it truncated the journal), removes
   records created by unproven writers, clears their tombstones, and seeds surviving
   committed tombstones for pruning. It then reserves a storage sequence as the
   recovered floor, above every pre-restart stamp.
4. The engine scrubs its attached indexes with that same plan in a durable
   storage bracket.
5. `CompleteRecovery` checkpoints last, before sessions can begin. Truncating
   earlier would erase the lifecycle records needed to classify index entries.

**The scrub writes on top of redo-only physical recovery (storage format 3, #1253).** Step 1's
physical recovery replays each page from its full page image and the committed deltas after it,
overwriting every stolen write of a bracket that never committed, and writes each rebuilt page
with the LSN of the last record it applied (`Database.Storage` DESIGN.md, "Recovery replay
rules"). The journal is not truncated until step 5, so the redo point stays where the open set it,
normally at the journal's checkpoint: the scrub's brackets in steps 3 and 4 touch pages above it,
journal no new page image, and chain their deltas onto the recovered LSNs. (A recovered page at or
below the header's LSN floor, which a non-checkpoint header write leaves above the checkpoint, is
imaged again instead; either way the next open rebuilds it from the journal.) A crash during the scrub leaves the original
records followed by some of the scrub's; the next open replays both through the same chain, which
rebuilds the pages the first scrub changed, and scrubs again — idempotently, since the stamp
checks skip versions already removed. `TransactionCoordinatorScrubCrashTests` cuts power at every
write the scrub makes (journal drains and pages it steals through a three-page pool, whole and
torn) and checks after each that the reopened coordinator scrubs every writer and keeps the
committed version, and that the scrub's records are deltas based on the recovered LSNs with no new
image.

During normal operation, begin/commit/abort appends and changes to the log's
active and writer sets share one monitor with checkpoint capture and truncation.
A begin cannot land between capturing the checkpoint's list and truncating
the journal. The storage writes that list twice: into a new generation of its file
header, made durable before the truncation, and into the checkpoint record appended after
it. The header copy is the one that survives a lost checkpoint record, and it holds any
number of sequences (`Database.Storage` DESIGN.md, "Checkpoints").

**Only writers are listed (#1242).** Recovery must classify every transaction whose row
versions can be in the data pages, and only one that applied a statement can have stamped
any. The log therefore tracks, besides the active set, the writers: a transaction joins
when its first statement enters the apply gate (before its storage bracket begins), and
leaves with its commit or abort record; a rolled-back writer whose undo is deferred stays
until its abort record is appended. A checkpoint lists the writers alone, so readers cost
the anchor nothing and a read-heavy load no longer pushes it past a capacity. A reader's
begin record goes with the truncation; if it later applies a statement, the log first
appends its begin record again — under the same monitor, so no checkpoint can intervene —
and the journal names it before its first stamp exists. The log knows which active
transactions the current journal names (begun or announced since the last checkpoint, or
listed by it), and resets that knowledge to the listed writers at every checkpoint, whether
or not the checkpoint completed, since a failed checkpoint may still have truncated.
`TransactionCoordinatorRecoveryTests` covers both: a checkpoint with two readers and a writer
lists the writer only, and a reader that writes after a checkpoint and then crashes is
announced, classified aborted and scrubbed (with the announcement removed, its version reads
as committed). `TransactionCoordinatorRollbackTests` anchors 1,000 writers — beyond the old
980-entry cap — through a lost checkpoint record and scrubs every one, and
`TransactionCoordinatorCheckpointCrashTests` cuts power at every write of a coordinator
checkpoint (data pages, the header slot, the truncation, the checkpoint record; whole and torn)
with 40 writers and a reader in flight: after each crash every writer is classified aborted and
scrubbed, the committed version stays visible, and LSNs keep increasing. Listing an empty
anchor instead fails it.

Commit appends and removes its sequence under that monitor,
then calls `EnsureDurable` outside it; a checkpoint that truncates first has
already durably flushed the outcome. The manager keeps the transaction active
until durability completes, or ends it as committed when the flush fails after the
append ("A commit record that was written but not made durable", above). This ordering has not been replaced with an
independent manager-table snapshot.

Statement apply, logical undo, and pruning share one semaphore. Engine conflict
locks are acquired before statement apply, keeping genuine lock waits outside
that semaphore and visible to deadlock detection. **The coordinator's checkpoint takes it
too (#1254).** A storage checkpoint runs only while no storage bracket is active, and every
bracket the coordinator opens runs under the semaphore, so `Checkpoint(CancellationToken)`
waits for the statement applying now and runs before the next one is admitted. Without it a
sustained statement load kept a bracket open almost all the time, the engines' checkpoint
workers were refused as busy on nearly every pass, and the journal grew without bound; the
engines' sustained-write tests (journal under four times a 4 MiB size) and the 1.5 GiB SQL
measurement (peak 256.4 MiB at the 256 MiB default, `Database.Storage` DESIGN.md,
"Measurements (#1254)") rest on it. Statement brackets commit non-durably unless the caller
selects the existing durable DDL/bootstrap path; the logical commit makes earlier statement
records durable by journal ordering. Open-time scrub remains ungated because no sessions exist
yet.

**The engines' checkpoint workers never wait for the semaphore (#1254 review).**
`Checkpoint(CancellationToken)` waits for the statement applying now, and one statement can run
for minutes (an index build or an `INSERT ... SELECT` applies in one bracket). Each engine has
one checkpoint worker that visits its databases in turn, so that wait stopped every other
database's checkpoints for as long as the statement ran; a review probe grew a second database's
journal to 130 times a 4 MiB size in six seconds. The workers call
`TryCheckpoint(TimeSpan.Zero, …)` instead. It checkpoints at once when the semaphore is free.
When a statement holds it, it records a deferred request and returns false, and the statement
runs the checkpoint as it ends, in `ApplyStatementAsync`'s `finally`, before it releases the
semaphore: the busy database is checkpointed the moment its statement ends, whatever the
statement's length, and the worker moves on to the others at once. The deferred checkpoint never
fails the statement that runs it, whose outcome is already decided: a storage bracket still open
outside the semaphore leaves the request for the next statement or the worker's next look, an
offline storage drops it, and any other failure is kept and thrown by the next `TryCheckpoint`,
so the worker records it (since #1268 the workers catch it per database, report it and retry;
before, it escaped the worker's loop and ended the worker). A deferred request reported as
`false` leaves an earlier failure of the worker recorded until a checkpoint of that database
completes. Short statements still cannot keep a checkpoint out, since every one
of them ends by running the deferred request. `TransactionCoordinatorRecoveryTests` covers the
deferral and the failure hand-off, and the SQL engine's
`CheckpointJournalSize_LongStatementInAnotherDatabase_ShouldKeepTheJournalBounded` holds one
database's gate while another is written past ten sizes, with the written database's journal
under four sizes and the held one checkpointed as its statement ends.

**A checkpoint is refused inside a statement apply.** The semaphore is not reentrant, so a
checkpoint asked for from an `ApplyStatementAsync` callback would wait forever for the semaphore
its own caller holds. The coordinator marks the apply's asynchronous flow (an `AsyncLocal`) and
`Checkpoint` and `TryCheckpoint` throw `StorageTransactionException` there instead; before the
review the call hung.

Logical undo, pruning, and recovery scrub apply at most 64 record/index mutations
per physical bracket. A blob transaction can contain many thousands of chunks;
retaining every touched page's pre-image in one undo bracket would otherwise
buffer the entire object (the storage also bounds a bracket's pre-images since #1253, by
spilling them to the journal past a budget, but batching keeps undo brackets short). Each
batch commits before the next begins. Stamp checks
make retries idempotent even when earlier batches committed before a later batch
failed: the full ledger is requeued, already-undone versions are skipped, and the
remaining work completes. Recovery scrub keeps at most 64 replacement payloads in
memory. The ledger and prune candidates still scale with record count.

Transaction recovery consumes `StorageJournal.ReadSequential`. Since #1257 every
journal it reads is a `StorageJournal` (the `IStorageJournal` interface, and with it the
`ReadAll` fallback for custom journals, is gone). Only sequence classification survives
iteration; physical page-image payloads are not retained.

The safe prune bound starts at `max(manager.OldestActive, recoveredSequenceFloor)`
and is reduced to every open context's `Snapshot.Minimum`. A snapshot captured
while an older writer was active can retain a floor below the current oldest
active transaction, so using only the manager's bound would reclaim visible data.
The purge pass retries failed abort undo before pruning, exactly as before. A retry that
fails again no longer ends the pass: the pass still prunes, and rethrows the retry's
failure at its end. A writer still deferred stays in the active table, so the bound never
passes its sequence and the prune reaches only committed tombstones older than it; before
this, one undo that kept failing stopped all reclamation.

### The coordinator's test hooks (#1257)

The coordinator takes `Storage` and `StorageJournal`, the abstract bases, since the
`IStorage` and `IStorageJournal` interfaces were deleted. Three coordinator tests'
storage and journal doubles used to intercept the coordinator's calls through those
interfaces: they re-implemented `IStorage.Checkpoint` and
`IStorage.ReserveTransactionSequence`, and decorated the journal to reject abort records.
`Storage.Checkpoint`, `Storage.ReserveTransactionSequence` and
`StorageJournal.AppendRollback` are non-virtual, and a test cannot hand `Storage` a journal
of its own, so the interception moved to the type that makes the calls
(`database-area.md`, "Test doubles"). The coordinator carries three internal hooks, reached
only through this package's `InternalsVisibleTo` grant to its own tests:

| Hook | Where it runs | Used for |
|---|---|---|
| `BeforeCheckpoint(long[] writers)` | under the lifecycle append gate, just before `storage.Checkpoint(writers)`, inside the same `try` | the writers a checkpoint lists, a checkpoint held open against a concurrent lifecycle append, a failed checkpoint |
| `SequenceReserved()` | in the manager's sequence allocator, after the storage reserved the sequence and before the begin record's append | a begin that reached the coordinator while a checkpoint holds the append gate |
| `BeforeAbortRecord(long sequence)` | inside the `try` of the abort record's append, before the journal append; a throw rejects the record | a lost abort record on a storage that stays online |

Each is `null` unless a test sets it and runs once per checkpoint, sequence reservation or
abort record, never per row. Each rewritten test asserts that its hook fired: the
`Checkpoint` helper of the rollback tests starts its capture unset and requires it set.

**Stream-level triggers cover the rest.** #1252 made every journal append a write into a
user-space buffer, so a failed journal write now happens at a drain, and a failed drain
takes the storage offline. The tests of an undo whose journal writes fail
(`RollbackAsync_StorageWentOfflineBeforeTheUndo_…` and
`RollbackAsync_UndoRecordsLostAtTheNextDrain_…`, which replaced
`RollbackAsync_UndoBracketJournalWriteFails_…`) fail the journal's backing stream
(`FaultingMemoryStream.FailWrites`) and need no hook. The abort-record hook stays because
no stream-level failure rejects one record and leaves the storage online.

## Non-goals

- No distributed transactions / two-phase commit — single-node ACID first.
- No lock escalation policy in the contract — an implementation concern.
- No savepoints in the MVP surface — add to `ITransactionContext` when a model needs them.

## AOT posture

Static composition, contracts and value objects; `FrozenSet<ulong>` for the active
set. The shared composition and engine adapters use ordinary calls and BCL
synchronization. No reflection, dynamic code generation, or `Microsoft.Extensions.*`.
