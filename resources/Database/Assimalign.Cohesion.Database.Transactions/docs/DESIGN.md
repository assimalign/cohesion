# Assimalign.Cohesion.Database.Transactions — Design

## Intent

One transaction substrate for five engines. ACID is the platform's defining requirement (area `DESIGN.md` R1), so the machinery that provides it — MVCC snapshots, commit ordering, locking, WAL binding — lives in a kernel project no model owns. Engines *use* the manager; sessions *expose* the resulting `DatabaseTransaction` from the contract root.

## Why MVCC (and not lock-based isolation)

Readers never block writers and writers never block readers — the OLTP profile all five models share. Locking is retained only where MVCC cannot arbitrate: write-write conflicts, in `LockManager`, with intent modes so object-level operations (drop table, reindex) coexist with entry-level writes.

## A child root — no area dependency

This package is a child root the area root aggregates (root → Transactions,
never the reverse — the 2026-07-13 inversion; see the area DESIGN.md decision
log). The transaction vocabulary lives here — `TransactionId`,
`TransactionState`, `TransactionSequence`, `IsolationLevel` — and the root's
`DatabaseTransaction` base consumes it through the root's child-root
reference. The contracts in this package speak only Transactions-owned types:
the doc-level nods to the root's `DatabaseTransaction` are *named*
(`<c>`), not referenced (`<see cref>`), and the adaptation between
`TransactionContext` and the public `DatabaseTransaction` surface belongs to
whoever owns both vocabularies — the model engines' session/transaction
implementations above the root (the area's standing cycle-avoidance shape). `Storage`
is the one reference (child-to-child): the journal/page
substrate the implementations bind to. The per-database composition below adds
no project or package dependency, including no dependency on Indexing.

## Type shape: sealed kernel types, three abstract seams (#1257, #1258)

Storage's raw page allocation and free methods are internal (#1270). Transactions tests
that construct page workloads use `AllocatePageForWrite` and `OpenPageForWrite` in committed
physical brackets, including the checkpoint latency probe; no cross-assembly visibility grant
or unjournaled setup is needed.

The area is concrete-first (`.claude/rules/database-area.md`); the project declares no
interface since phase 2 of the concrete-types program
(`docs/programs/DATABASE_CONCRETE_TYPES_PLAN.md`).

- **Sealed, with internal constructors:** `TransactionManager` (since #1257) and
  `TransactionContext` (the most-consumed kernel contract, so every call on it is direct).
  Each has one implementation and is created here by `TransactionManager.Create` and
  `TransactionManager.BeginAsync`, respectively. `LockManager` is sealed with a public
  parameterless constructor (`new LockManager()`, owner decision 61; "The lock manager
  implementation", below).
- **`TransactionContext.PinStatementSnapshot()`** returns a statement view: it shares the
  transaction's id, sequence, isolation level and state, and fixes the snapshot at the call.
  Documents, Graph and Blob pin one per read-committed operation, so every metadata lookup and
  every chunk read of the statement makes one visibility decision; each used to carry an
  identical private decorator for it. Sql and KeyValuePair take no view: their statement
  context captures the snapshot once and the statement runs under the transaction's own
  context, because phase two must re-read that context's fresh snapshot where a write waited
  for a lock (a Sql cascade's index deletes did, #1363 review, until #1370 matched them by
  their stamps instead). The view keys brackets and stamps by the shared sequence,
  and a manager refuses to commit or roll back a view. The end claim and the apply admission
  live on the transaction's own context only; a view forwards them, so an apply admitted
  through a view is one the transaction's end waits for.
- **Abstract, for a variant set or an inverted seam:**
  - `TransactionRecordSpace` (protected constructor): the five model storages each supply one
    from their own assemblies, and the coordinator drives them;
  - `RecordVersionIndex` (protected constructor): Indexing's `BTreeRecordVersionIndex` and the
    Documents.Catalog and Graph.Storage index-undo adapters, which the version store's ledger
    calls back;
  - `VersionStore` (`private protected` constructor): both leaves, `RecordSpaceVersionStore` and
    the in-memory store `VersionStore.CreateInMemory()` returns, live here, so only this
    assembly and its own tests (through the `InternalsVisibleTo` grant) can derive.

  Each public member is non-virtual and calls a `protected abstract …Core` member. The public
  member checks what every leaf checked before: a null storage transaction for the adapters, a
  canceled token for `AppendVersionAsync`, and a null snapshot and a canceled token for
  `GetVisibleVersionAsync`. `PruneAsync` and `PurgeWriterAsync` check nothing in the base,
  because the two stores observe cancellation at different points.

## Recovery cancellation (49a)

`TransactionRecovery.Analyze`, `TransactionCoordinator.AnalyzeAndScrub` and `CompleteRecovery`
accept cancellation. Journal classification and the batched record scrub observe it between
records; checkpoint entry observes it before its atomic checkpoint. Scrub commits may precede
cancellation. A caller must preserve the classification journal until all recovery stages
complete: SQL marks an interrupted open `RecoveryInterrupted` before closing the unpublished
storage, preventing its normal shutdown checkpoint. The next open reclassifies and finishes
scrubbing instead of treating the remaining abandoned stamps as committed.

## Version lookup integrity (#1160)

`VersionStore.GetVisibleVersionAsync` returns absence only for a reclaimed location or an
invisible version. A checksum failure propagates `StorageCorruptionException` with
`CK06003` and the page id; the record-space test corrupts media after eviction and proves
both failure and a successful lookup after repair. Engines translate the storage failure
at their own boundary. The short-record rule is tracked separately by #1372.

## Identity vs. ordering: `TransactionId` vs. `TransactionSequence`

`TransactionId` is a GUID — a good *external* identity for sessions, diagnostics, and the wire protocol, but unordered. Visibility decisions need a total order, so this project also has `TransactionSequence`, a monotonically increasing `ulong` assigned at begin. Readers and writers both keep storage-counter sequences; no virtual identifier is introduced, and the public session surface remains free of MVCC mechanics.

## Snapshot semantics

`TransactionSnapshot` captures `(owner, minimum, maximum, active-set)` and answers `IsVisible(writer)`:

- own writes → visible;
- `writer >= maximum` (began after capture) → invisible;
- `writer < minimum` (decided before the oldest in-flight) → visible;
- otherwise → visible iff the writer was not in the active set.

Note the deliberate simplification: a version whose writer *aborted* below `minimum` must never be consulted, so the version store — not the snapshot — is responsible for unlinking aborted versions during rollback/recovery. That keeps the snapshot a pure value object with no commit-log lookup. This duty is a contract member: `VersionStore.PurgeWriterAsync(writer)`, called by the manager on rollback/abort and by recovery for every sequence the journal cannot prove committed.

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
flush failed is different, and is described next. `OldestActive` is
`min(active)`, or `lastAssigned + 1` when idle. It is not a pruning bound on its own: the
internal `PruneBound` also takes the minimum of every active snapshot or serializable
transaction's snapshot, under the lock the begin holds while it captures that snapshot
("Shared per-database composition", below). A manager rejects a
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
(`StorageOfflineException`, `CK06002`; `Database.Storage` DESIGN.md, "A failed durable flush
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

**One cancellation rule for commit and rollback (#1246).** The caller's token is observed
only before the manager claims the end. A token canceled by then throws
`OperationCanceledException` without claiming the end, changing visibility, undoing writes,
releasing locks or writing a lifecycle record; the transaction remains usable. Once claimed,
the end ignores caller cancellation while it drains admitted applies and appends or flushes
the log. Commit still reports real append failures as `TransactionAbortedException`, or a
written record whose durability failed as `TransactionCommitUnconfirmedException`; cancellation
cannot turn a started commit into either failure or a `CommitRecordWriteFailed` event.
The rule includes read-only contexts, which still append and flush nothing (#1280).

Every model's explicit transaction uses the root end state machine. Automatic commit also
owns an uncancellable end once execution has completed: SQL and KeyValuePair pass no caller
token to the coordinator, matching Documents, Graph and Blob. SQL catalog DDL that self-committed
finishes mandatory cleanup without caller cancellation and delivers its committed result. Statement execution
and lock waits continue to observe their tokens before that automatic end.

This rule covers the in-process result. A wire connection that dies after commit may drop the
outcome frame: SQL and Key-Value server sessions stream results with the connection token.
The durable commit cannot guarantee delivery over a dead connection.

SQL and Key-Value execution-completion tests cancel inside the statement apply callback after
execution, before coordinator/log commit entry, and prove the session fix through success,
affected count and reopen persistence. Documents, Graph and Blob cancellation tests and the
explicit-transaction engine cases are regression guards; their late-flush cancellation was
already handled by the old session path.

PostgreSQL likewise holds interrupts through commit cleanup:
`src/backend/access/transam/xact.c:2382-2383` (`HOLD_INTERRUPTS`) through
`:2548` (`RESUME_INTERRUPTS`). The Cohesion boundary is the manager's end claim, shared
with rollback and disposal. Its lock order is manager `_sync` then context end latch;
both are released before apply draining or journal append/checkpoint gates are entered.

A rollback, once it starts, ends its transaction whatever fails or is canceled. The
start is a claim: `CommitAsync`, `RollbackAsync` and the abort that disposal forces
each claim the context's end first, so exactly one of them runs. A second attempt
while one runs fails with `TransactionAbortedException` ("already ending"), and a
commit can never race a rollback of the same writer. The same claim closes the
context to statement applies ("Ending a transaction under a running statement",
below): the context carries one end flag, so the end that wins the claim is also the
end every later apply is refused for. Both end methods observe the caller's token only
before the claim: a token canceled by then throws `OperationCanceledException` and
leaves the transaction active and untouched. After the claim no token reaches the
rollback, and it throws nothing:

1. **Drain.** The rollback waits for the statement apply already admitted for the
   context, if one is running. The wait observes no token and is bounded (nothing
   awaited inside the apply gate may actually wait), so it cannot stop a started
   rollback; a commit drains the same way before its commit record. A failed
   statement's bracket rollback inside the gate does wait for one thing: the readers of
   each B-tree the bracket changed, whose latch it holds while it restores the tree's
   pages (#1371, `Database.Storage` DESIGN). A reader holds that latch only while it
   materializes a range and waits for nothing the gate holds, so that wait is bounded too.
2. **Undo.** `VersionStore.PurgeWriterAsync(writer)` removes the writer's versions.
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
(S/U/X/IS/IX), every mode an owner was granted kept beside the others, same-owner
upgrades (an owner's own grants never block it), a FIFO queue per resource in which a
request waits behind the conflicting requests ahead of it (a holder's request goes ahead
of the requests that wait for it), FIFO wake-up whenever a grant or a queued request
leaves, and wait-for-graph deadlock
detection: a request that would close a cycle aborts *itself* with
`TransactionDeadlockException` — the
newest-waiter-as-victim policy, chosen because it needs no cost model and the victim
is retryable by construction. Waits are `TaskCompletionSource`-based and honor
cancellation. `ReleaseAll(owner)` ends the owner's participation in the table: it
releases the owner's grants and fails the owner's own queued requests with
`TransactionAbortedException`, because a grant arriving after the owner ended would hold
the resource for a transaction that can never release it (see "Ending a transaction
under a running statement"). The lock manager also has an internal
`AbandonPending(owner)`, the first half alone: it fails the owner's queued requests and
keeps its grants, for the end of a rollback whose undo is deferred ("Ending a
transaction"). Until `ReleaseAll` the owner stays abandoned: `AcquireAsync` refuses its new
requests with `TransactionAbortedException` and `TryAcquire` returns false, except for a
request the modes it already holds on the resource cover (every mode the request conflicts
with already conflicts with one of them), which changes nothing for anyone else. No public
member was added for it.

**Per-wait timeouts (#1231).** EnterLockTimeout(TimeSpan) is a public, flow-local disposable
scope: zero disables it, nested scopes restore the previous duration, and concurrent flows have
independent settings. It covers direct and nested unique-index requests in standalone and engine
modes, without a shipped visibility grant. Durations must be non-negative and at most int.MaxValue
milliseconds, matching PostgreSQL's bound (guc_parameters.dat:1617-1625). Only a contended wait
creates a timer source; each wait gets a new budget, as
PostgreSQL's ProcSleep does (src/backend/storage/lmgr/proc.c:1407-1425). A timeout removes the
queued request and throws TransactionLockTimeoutException (SQLSTATE 55P03). The caller owns
rollback and release of the owner's earlier grants. Caller cancellation and storage abandonment
keep their existing meanings. Separate caller, timer and abandonment registrations carry their
cause into queue removal under `_sync`; the first removal fixes both the exception and event
outcome, even if another deadline expires before the continuation resumes. This follows
PostgreSQL's earlier-deadline rule (src/backend/tcop/postgres.c:3590-3600).
The apply-gate refusal still runs before any wait: lock waits precede the coordinator's apply
gate and storage/index latches. Cancellation callbacks take only the lock table's _sync;
registrations/timers are disposed outside _sync. Grant/end and cancellation now arbitrate under
_sync: cancellation can complete only a request it removed, so it cannot replace a grant whose
notification has not yet run. LockTimeoutTests checks Timeout events, independent flows, scope
restoration, individual budgets and other end reasons. Per-manager internal hooks control timer
expiry and pause after grant dequeue but before notification; the arbitration test cancels both
tokens in that interval and counts that interleaving, requiring grant as its sole outcome.

**Unwritten statement marks (#1668).** LockManager.MarkStatement(owner) returns an opaque,
allocation-free LockStatementMark with value equality over manager identity, owner and grant
boundary. Each resource's first grant to an owner carries a monotonic
stamp beside its mode mask; a mark records the manager, owner and current stamp. Re-grants
and upgrades keep that first stamp. ReleaseSince(mark) removes only resources whose first
grant is newer, visits only that owner's resource list, and wakes the same FIFO queues as
ReleaseAll. It does not end the owner. Existing resources keep every mode, including a new
upgrade: no downgrade is attempted. Default/foreign marks are refused; callers serialize the
owner's lock requests, have no pending request when releasing, and use a mark only within
one transaction lifetime. An abandoned writer retains every grant for deferred undo.
MarkStatement reads the grant sequence atomically without taking the manager-wide lock. The
caller serializes the owner's requests, so all its earlier grants have completed and no later
one can race the mark; concurrent grants to other owners do not change that owner's boundary.
First grants advance the sequence with Interlocked.Increment, paired with Interlocked.Read.
The mark/re-grant allocation guard runs only in Release, with tiered compilation disabled in
this test project, as SQL already does. Its populated-table, uncontended loop still requires
exactly zero bytes over 1,000 operations after 100 warmups; runtime tier transitions cannot
define the production allocation contract. The Linux failure after #1689 reported 1,792 bytes;
Initial unpressured Windows runs did not reproduce it, including with tiering enabled. The optimized path adds no
entry, owner list, waiter or grant and allocates no product object. A forced 16-byte-array
allocation per mark fails the guard before restoration. This changes test execution, not
locking semantics. PostgreSQL likewise handles an already-held lock through its local grant
path (src/backend/storage/lmgr/lock.c:969-982); no reference code is copied.

**Exact counters also exclude background-GC accounting (found during #1288).** Tiering
control alone is insufficient: the Storage map's new zero-byte guard passed alone but
reported 7,288 bytes in the full Release suite. A standalone .NET 10.0.13 win-arm64
control measured only counter reads and `SpinWait`, yet background GC produced four
8,136-byte deltas in 256 intervals; the same executable with concurrent GC disabled
reported zero in every interval. Explicit allocation controls were 64 bytes in all
32 samples in both modes. Replacing `SpinWait` with the existing 1,000 mark/re-grant
loop produced seven nonzero intervals out of 2,048 with background GC, and none
without it. The zero-byte assertion and operation count therefore remain unchanged;
this test host disables concurrent GC and checks the actual `ConcurrentGC` runtime
configuration. A real-allocation positive control remains observable. Runner parallelism
is uncapped; model-engine suites retain their own GC configuration.

This matches the upstream accounting defect [dotnet/runtime#134724](https://github.com/dotnet/runtime/issues/134724)
and its proposed [fix #134855](https://github.com/dotnet/runtime/pull/134855).
The consulted runtime source computes the counter after subtracting the unused context
(`src/coreclr/vm/comutilnative.cpp:859–868`); the normal collection path retires those
bytes (`src/coreclr/gc/allocation.cpp:1123–1130`), while background context clearing
zeroes its pointers (`:1149–1158`). No runtime code is copied or changed. The test-only
configuration gives a platform-independent measurement without an allowance, a minimum
of retries or suppressing product allocations. The runtime fix remains an upstream follow-up.

This operation performs no undo: it releases only after unwritten arbitration or a failed
statement bracket that has already been fully restored physically (#1231). Surviving writes
retain transaction-duration locking. SQL ON CONFLICT uses it to
release its whole unapplied arbitration before an AcquireAsync row wait, then keeps the
wait's row grant and takes a new mark before restarting. Each restart retains a superset of
the rows it waited for, whose previous holders have finished; re-granting its own row cannot
start another symmetric hand-off. Later back-offs keep earlier waited rows too. The wait remains an edge
in the detector, with existing cancellation and abandonment handling; no transaction-end
primitive is needed. Snapshot rejects a committed changed row; ReadCommitted re-arbitrates;
rollback undoes stamps before release. Arbitration has made no heap/index or ledger change,
opened no data apply bracket and projected no RETURNING row. Only defaults/identity allocations
may already have side effects; replay deliberately evaluates them again. The final apply
retains ordinary transaction-duration locks. Pre-statement grants/upgrades survive, and
reversed multi-row key acquisition can still deadlock. Multi-row upserts retaining waited rows
in opposite orders can also deadlock row-against-row; the detector chooses a retryable victim,
the residual exposure of avoiding livelock (PostgreSQL execIndexing.c:85-95). PostgreSQL's reference waits and
rechecks in src/backend/executor/execIndexing.c:882-901 and restarts after row changes in
nodeModifyTable.c:1146-1172,2850-2874. No new public interface or visibility grant is needed.

**An owner keeps every mode it was granted (#1390 review).** The table keeps a mask of the
modes each owner holds on a resource, and a request conflicts with an owner when any of its
modes does; PostgreSQL keeps the same mask per holder (`holdMask`,
`src/backend/storage/lmgr/lock.c:1702-1709`). Until the #1390 review it kept only the
strongest mode by a fixed order (IS, S, IX, U, X), which the five modes do not have.
Intent-exclusive then update kept update, which allows shared, and lost intent-exclusive's
conflict with it: a shared request queued before the update still waited, since nothing woke
it, but the detector, which reads its edges from the table, found none, and a cycle through it
went unseen (the #1390 review's scratch test saw no deadlock within 3 s on `a60f850c`, where
`19b1a495`, whose edges were recorded at the request, aborted the requester in 74 ms; the
committed test times out at 5 s on `a60f850c`). Intent-exclusive then shared kept
intent-exclusive, and another owner's intent-exclusive, which shared conflicts with, was
granted beside it, on both commits. The shipped engines never combined those modes on one
resource (objects take IS, IX or X; entries S or X), but `LockManager` is public with all five.
`LockManagerTests` holds both cases.

**The wait-for graph is read from the table (#1390).** Until #1390 the detector kept a graph
of its own: a request that queued recorded edges to the owners holding the resource at that
moment, and the edges stayed until the owner's wait ended. But the holders a waiter waits for
change while it waits. A release wakes the request queued ahead of it and grants that request
the resource; a compatible request was granted past it (a grant depended on the modes held,
never on the queue, until the #1390 review); the holder it first waited for leaves. The recorded edges then named
transactions that had left and missed the ones that came, so two transactions could wait for
each other with neither edge in the graph, and both waited until something canceled one of
them. A granted or canceled request also dropped every edge of its owner, those of the owner's
other queued requests with them. The #1370 review's SQL stress runs saw two to five waits per
run pass 10 s. The committed probe of that workload (`SqlLockWaitTests` in `Database.Sql`,
Release, 60-second runs) was run twice on `19b1a495` with a diagnostic that reported each wait
still queued after 1 s: the holders the table showed, whether the recorded graph had an edge to
each, what each holder was queued for in turn, and whether the table held a cycle through the
waiter. Each run saw 40 waits pass 10 s and none end between 1 and 10 s, and all 80 are
accounted for. Twenty were the members of ten two-transaction cycles; the other 60 were queued,
directly or through a chain, behind a member. In every cycle one member's recorded edge named a
transaction that had released the resource, which another transaction now held, so the graph
had one edge of the cycle and the detector never saw it. Each cycle lasted until the probe
canceled a wait at 12 s. Nine were the referential cycle the Sql engine's lock order allows (its
DESIGN.md, "Referential enforcement locks parent rows"): a cascading `DELETE` holds a parent row
and waits for a child row, while a child writer holds the child row and waits, in its
foreign-key check, for the shared lock on the parent row. In one of them an `UPDATE` of the
child had queued for the parent row while another transaction held it; that transaction's
release granted the row to the cascade, queued ahead of the update, and the update's only edge
still named the transaction that had left. The tenth was two child-table writers in explicit
transactions, each holding the row the other's next statement needed. In seven of the nine
referential cycles the child writer's edge was the stale one, in two the cascade's. Every
transaction a long wait reported was itself in a statement
waiting for a lock of this table, never committing, between statements or idle, and every queued
request had a conflicting holder: no long wait came from a lock held across another kind of
wait, and none from a lost wake-up. With the fix, five 60-second runs of the same probe saw no
wait reach 1 s (72,674 to 81,287 commits, 24,948 to 28,165 lock waits and 1,256 to 1,447
deadlock victims a run), nor did a 300-second run of the fixed lock manager under the base's Sql
engine (298,671 commits, 5,294 deadlock victims).

Now the lock manager keeps only each owner's queued requests, and the search reads the edges from
the table as it stands. The requester waits for every owner that holds, in a conflicting mode,
a resource one of its queued requests is for, and for the owner of every conflicting request
queued ahead of it ("Grants respect the queue" below). The search follows every queued
request of each owner. A cycle first tries queue repair (#1420); an unavoidable held-lock
cycle still chooses the requester as victim. PostgreSQL likewise reads its current lock table
(`src/backend/storage/lmgr/proc.c:1880–1891`), distinguishes hard holder edges from soft queue
edges (`deadlock.c:561–583,624–768`), and tries queue reorderings before aborting.

**Soft-cycle repair (#1420).** Under `_sync`, construct a dependency graph containing every
conflicting held-lock edge of every queued request. A cycle in those hard edges cannot be
repaired by queue movement: return without changing any live queue. Otherwise retain existing
conflicting FIFO edges greedily, adding one only when it cannot close a cycle in this graph.
Stable topological owner ranks then give consistent orders to all affected resource queues,
including owners waiting on multiple resources. An omitted edge already has the reverse path,
so the resulting order reverses the dependency that could not coexist with retained edges.
Every final hard or queue edge follows the same acyclic rank order. Queues without a detected
cycle retain their ordinary FIFO/holder placement rules.

This follows PostgreSQL's queue-reordering behaviour (`src/backend/storage/lmgr/deadlock.c:310–370,860–996`)
without copying its backtracking search. The polynomial graph construction preserves a maximal
consistent set of FIFO edges, rather than searching for a minimum number of moved waiters.
No new grant is permitted against an incompatible holder. Queue moves keep the original waiter,
completion, cancellation and timeout registrations; notification remains outside `_sync`.
A queue's private order version restarts a wake scan if grant-time repair reordered it.
A hard-cycle failure leaves all queue orders intact. Temporary graph storage is allocated only
after a detected cycle; no hot-path process-wide atomic or public API is added.

`LockQueueReorderTests` checks repair across the five modes, multiple queued requests per owner,
hard-cycle refusal without partial movement, and cancellation after dequeue/before notification.
The prior #1390 test that required a soft-cycle victim now requires repaired completion while
retaining actual held-lock blockers. Hard-deadlock tests remain. Neo4j verifies actual held-lock
cycles before selecting a victim (`community/lock/src/main/java/org/neo4j/kernel/impl/locking/forseti/ForsetiClient.java:829–860`);
RavenDB instead queues writes on a merger thread (`src/Raven.Server/Documents/TransactionMerger/AbstractTransactionOperationsMerger.cs:101–134`).
Those ownership models were consulted; neither is copied into this shared lock manager.

A grant can add edges as well, from each request queued on the resource whose mode conflicts
with the one granted and with nothing the grantee held before, to the grantee. A queued request
granted by a wake-up adds none: a conflicting request behind it already waited for its owner, and
none ahead of it conflicts. A holder's new request granted at its place, ahead of the first
request that waits for it, can add one only from a request behind that place that conflicts
with neither the grantee's held modes nor that first request. Worked through the matrix for a
request blocked by another holder, no such request exists: whatever mode blocks it would
conflict with the grantee's modes or the mode granted. One blocked only by requests ahead of it
leads back to a holder the same way, or to a request that waits for the grantee, so it already
waits for the grantee. A scratch model check that drove a standalone lock manager through 80,000
random requests, releases, cancellations and `TryAcquire` calls over all five modes, two queued
requests per owner allowed, against an oracle of the queue (200 seeds of 400 steps), checked it:
of 7,331 grants of a holder's request ahead of a waiter, none gave an edge to a request that did
not already wait for the grantee, and none of 3,866 deadlock victims was chosen by a grant. It
also saw no grant at once the oracle refused or the reverse, no request left queued that could
be granted, no cycle left standing, no victim off a cycle and no two owners holding conflicting
modes. The
grant-time search the queue-jumping rule needed stays as a check: an owner may wait for two
resources at once (the public `AcquireAsync` allows it), so a grant to an owner with a queued
request that adds an edge searches from that request, and a request it finds on a cycle would
fail as the victim with `TransactionDeadlockException`, its `LockWaitStop` outcome `Deadlock`.
The search at the request sees every cycle the queue makes: every edge a request adds when it
queues starts or ends at its owner (its own blockers, and the requests behind its place that it
now blocks), and taking a request off a queue only removes edges.

**Every wait a lock holder makes is a lock of this table, or bounded.** The detector sees the
lock table only, so a transaction that holds locks must never wait, unbounded, for anything
else. The kernel's other waits keep to that:

- **The apply gate** of the coordinator (one statement applies at a time) is held while a
  statement applies its writes, and every writer, undo and prune of the database waits for it.
  A statement acquires every lock its apply needs before it takes the gate, and the locks its
  index maintenance requests inside the gate are re-grants that complete at once: a holder's
  request goes ahead of the queued requests that wait for it ("Grants respect the queue" below),
  and every request a re-grant conflicts with waits for it, so a queue cannot make a re-grant
  wait. The rule was
  documented and unchecked; the coordinator's lock manager now enforces it. A request that
  would wait while its flow holds the gate fails at once with `InvalidOperationException`, and
  the statement's bracket rolls back, instead of stalling every writer of the database behind a
  wait the detector cannot see. This is PostgreSQL's rule for buffer content locks, which have
  no deadlock detection (`src/backend/storage/lmgr/README:20-29`): `heap_delete` unlocks the
  buffer before it waits for the tuple lock (`src/backend/access/heap/heapam.c:2920-2923`).
- **The B-tree latch** (`StoragePageLatch`, `Database.Storage`) is never held across a lock
  request. `BTreeIndex` takes a unique key's lock before it enters the latch. A reader holds the
  latch only while it materializes a range, and a rollback that restores a tree's pages waits
  only for those readers.
- **Page write locks** never wait: a page another storage transaction has write-locked fails
  the write (`StorageTransactionException`).
- **The database writer lock** of the Graph, Documents and Blob engines is `LockResource.Database()`
  in this table, so its waits are in the graph.
- An end's drain of its own statement apply, the journal flush of a commit and a rollback's wait
  for the apply gate are bounded, because nothing they wait for waits for a lock.

**Grants respect the queue (#1390 review).** A request is granted when no other owner holds a
mode it conflicts with and no request of another owner queued ahead of its place conflicts with
it; otherwise it waits at that place. The place is the end of the queue, except for a request
of an owner that already holds the resource: it goes ahead of the first queued request that
waits for the owner (one whose mode conflicts with a mode the owner holds), which cannot be
granted before the owner ends anyway. A re-grant is therefore granted at once, since every
request it conflicts with waits for the owner. The queue is woken in order whenever a grant
leaves it or a queued request leaves without one (canceled, failed as a deadlock victim, or its
owner ended). That is PostgreSQL's lock manager: `LockAcquireExtended` queues a request that
conflicts with a queued one (`src/backend/storage/lmgr/lock.c:1136-1143`), `JoinWaitQueue`
places a holder's request ahead of the first waiter that waits for it and grants it there when
nothing ahead conflicts (`proc.c:1264-1314`), and `ProcLockWakeup` wakes the queue in order
after a release or a waiter's removal (`proc.c:1828-1863`). A holder that could pass the whole
queue would let owner after owner join in a mode a waiting request allows (intent-shared beside
a waiting intent-exclusive) and then upgrade past it (`LockManagerTests`, "A holder's request
queues behind a queued request that does not wait for it").

Until the review a grant depended on the modes held only, so a stream of compatible grants kept
a conflicting request waiting for as long as the stream lasted. The probe concentrated on one
parent row with one cascading deleter and fourteen child writers, every transaction explicit
and pausing up to 5 ms after each statement (`COHESION_DATABASE_LOCK_WAIT_PROBE_SHAPE=1,1,14,1`,
the review's starvation workload), showed it: on `a60f850c` two 60-second Release runs saw
twelve and twenty waits of 1 s or more, every one the deleter's exclusive request on the parent
row while the writers' shared grants passed it, the longest 11.3 s and 6.3 s; the deleter
committed one delete in the two. The review's own probe of that shape saw seven consecutive
waits of the deleter canceled at 12 s. With the queue no wait reached 1 s. The rule also lets a
DDL statement's exclusive table lock through a stream of writers' intent locks, which could keep
it waiting the same way. The #1163 guard refuses 1,000 later intent requests behind queued DDL;
SQL's concurrent DDL stress keeps all three writers running through catch-up and counts commits
during each exclusive table wait: only the already-admitted writers may finish.

**The queue does not make a hot row's cascade succeed, and it costs a hot row throughput.** The
same probe, run for 60 s in Release without tiered compilation, twice on `a60f850c` and twice
with the queue, alternating, on one machine (shape: parents, deleters, child writers, and
whether every transaction is explicit; each cell gives the two runs):

| Shape | Commits | Deadlock victims | Parent deletes committed | Waits of 1 s or more (longest) |
| --- | --- | --- | --- | --- |
| 6,3,5 (the default) | 74,630, 81,824 → 82,201, 80,961 | 1,360, 1,508 → 2,090, 1,962 | 10,805, 11,758 → 12,372, 12,269 | none → none |
| 1,2,8 | 133,714, 145,117 → 59,087, 58,763 | 1,918, 2,047 → 8,735, 9,344 | 458, 602 → 932, 587 | none → none |
| 1,1,14 | 148,034, 219,201 → 76,530, 78,566 | 586, 881 → 5,790, 6,033 | 6, 7 → 3, 0 | 14 (3.5 s), none → none |
| 1,1,14,1 | 25,650, 35,990 → 20,771, 23,089 | 139, 218 → 2,256, 2,526 | 1, 0 → 10, 0 | 12 (11.3 s), 20 (6.3 s) → none |

The #1390 review's four scratch workloads (unique and plain secondary indexes with two-level
cascades and checkpoints; the one-hot-row starvation shape; several rows per transaction with
index range updates; KeyValuePair writers on six hot keys), 90 s each with the queue, saw no
wait reach 1 s (the longest 523 ms) and no error but the conflicts each workload provokes; the
coordinator's refusal of a wait inside a statement apply never fired.

Before #1420, with one hot parent row the cascade starved on `a60f850c` by waiting, and starved with the queue
as the deadlock victim, with no long wait. The cascade's queued exclusive request now holds back
the child writers' shared requests on the parent row, and the writers queued behind it hold
child rows from their earlier statements. When the cascade is granted the parent row and asks
for those child rows, each request closes a cycle, and the cascade, the requester, is the
victim; the writers queued behind it waited for a statement that then rolled back. The Sql
engine's lock order makes the cycle (its DESIGN.md, "Referential enforcement locks parent
rows"), and the victim rule decides who loses it: PostgreSQL's victim is the waiter whose
`deadlock_timeout` fires first after the cycle forms (`CheckDeadLock`, `proc.c:1915-1932`), which
here would often be the child writer that queued first. PostgreSQL also takes a different rule
for this very lock: a foreign-key check's `FOR KEY SHARE` row lock does not sleep while no key
update or delete has reached the row, so it passes a deleter that is still waiting
(`src/backend/access/heap/heapam.c:4914-4950`), as this table's grants did before. The queue is
kept because it bounds every wait (the #1390 gate) and is the rule of PostgreSQL's,
SQL Server's and InnoDB's lock managers; letting a parent row's shared requests pass a queued
exclusive one again, a victim rule that spares the cascade, or a cascade lock order without the
cycle required an owner decision. D2 chooses the unwritten cascade release/wait/retain/restart
protocol, using #1668 statement marks, while preserving queue fairness outside soft-cycle repair.
The 2026-10-24 owner decision bounds this protocol per cascade statement: the first two
refusals release new attempt grants; from the third refusal onward the statement retains
its closure grants and queues normally on each refused resource. Completed wait grants
remain retained throughout. This prevents repeated release/rebuild from admitting hot child
inserters indefinitely; it does not suppress genuine held-lock deadlocks or change victims.
The shapes above remain the before/after measurement workloads.

**A wait still queued at the threshold is reported (#1390).** `SlowLockWait` is written as a wait
ends, so a wait that never ended was never reported. A request that queues while a listener takes
the source's warnings arms a one-shot timer for the slow-lock-wait threshold. If the request is
still queued then, it writes `LockWaitBlocked` once (event 19, "Diagnostics" below), naming the
transactions it waits for (holders, and the owners of the requests queued ahead of it that it
waits behind) and what each of those has queued in turn. That report is PostgreSQL's
`log_lock_waits` "still waiting for" message (`proc.c:1688-1704`). With no listener the request
arms nothing. The description is built under the lock table's lock and ETW drops an event
larger than 64 KB, so it is bounded (#1390 review): at most 16 blockers of the waiting request,
4 queued requests of each, and 4 blockers of each of those, the rest counted (`... (+N more)`,
`and N more requests`).

**Release visits only the owner's resources (#1363 review).** Beside the table, the lock
manager lists every resource each owner was granted or queued for, and `ReleaseAll` and
`AbandonPending` walk that list instead of the whole table. Waking waiters only there loses
nothing: a request waits only for the grants and the queued requests of its own resource, and
every grant and queued request of the owner is on a resource the list names, so no waiter
elsewhere becomes grantable when an owner leaves. Before, every transaction end enumerated the table under the
lock manager's lock, so its cost grew with every other transaction's locks and with the
table's high-water mark (a dictionary enumerates every slot it ever used): after one
transaction deleted 200,000 keys or rows, every later end paid for 200,000 slots with the table
empty. An auto-commit KeyValuePair `GET` took 158 µs and a Sql primary-key seek 321 µs then,
and 2.1 and 16 µs with the owner's list (in-memory engine, Release, four alternating process
pairs pinned to the same cores); with an empty history the two are equal within noise. A resource stays
listed after the owner's wait for it was canceled; the release then finds no grant or request
of the owner there. PostgreSQL's `LockReleaseAll` likewise walks the backend's own lock table
and proc-lock lists, never the shared table (`src/backend/storage/lmgr/lock.c:2338`, `:2377`,
`:2511-2540`).

The #1250 timing guard releases ten owned locks beside 1,000 versus 100,000 foreign locks.
It alternates sizes in one non-parallel process, compares the minima of five rounds, and allows
an eightfold ratio; a whole-table scan grows with the hundredfold increase in foreign entries.

**One sealed type, with an engine mode (#1258).** `LockManager` is a sealed class with an
internal constructor and `new LockManager()`; the `ILockManager` interface and the internal
`DefaultLockManager` it fronted are gone, so every manager has `AbandonPending` (before, a
lock manager of another type passed to `TransactionManager.Create` left a deferred writer's
queued requests queued). The coordinator used to hand engine code a private decorator over the
manager's lock manager; a sealed type cannot be decorated, so the decorator became an internal
mode of the one instance (plan §6.2), installed once by the coordinator
(`EnterEngineMode(manager.IsTracked)`):

- the public `ReleaseAll` releases nothing for a transaction the transaction manager still
  tracks, which is what keeps a rolled-back writer whose undo is deferred holding its locks
  (#1226); the manager releases through the internal `ReleaseAllUnfiltered` when the
  transaction leaves its active table;
- a request that has to wait also ends when the storage goes offline (`Abandon`, called by
  `TransactionCoordinator.AbandonLockWaits`, #1268), failing with `TransactionAbortedException`
  whose inner exception is the storage's offline error; a request granted at once costs what
  it did.

A lock manager from `Create` has no mode installed and behaves exactly as before. The #1226
lock-retention and #1268 abandon tests gate the change.

## The WAL binding

Storage owns the physical journal (`Database.Storage`); the transaction log is the *logical* seam: begin/commit/abort records with the write-ahead rule (commit acknowledges only after durability). Group commit is an implementation freedom, not a contract change. This split lets the transaction manager be tested against an in-memory log (`TransactionLog.CreateInMemory()`) while the real one rides the storage journal (`TransactionLog.CreateJournalBound(StorageJournal)` — commit appends and calls `EnsureDurable`, so concurrent commits naturally share fsyncs). `TransactionRecovery.Analyze(journal)` is the restart-side counterpart: a sequence committed iff its commit record is durable; everything else is aborted and must be purged from version stores.

**The log is internal (#1257).** `TransactionLog` is an `internal abstract` class in `Internal/`, and its two factories are internal statics on it: every variant (the in-memory log, the journal-bound log, the coordinator's gated journal log) lives in this assembly, and the only other derivations are this package's own fault-injecting test doubles, which reach it through the assembly's `InternalsVisibleTo` grant. The public `ITransactionLog` interface and the `public static class TransactionLog` factory are gone; only tests ever called the factories.

## Error model

`TransactionDeadlockException` and `TransactionCommitUnconfirmedException` are sealed leaves
(#1264). `TransactionAbortedException` remains the shared base for retryable abort variants.

A hard deadlock refuses the lock request; it does not roll back the victim transaction.
Under SQL statement atomicity, earlier explicit-transaction work and locks stay active
until the caller rolls the victim back, allowing surviving waiters to progress. SQL
translates the refusal to structured CS04065, mapped to SQLSTATE 40P01.

`TransactionAbortedException : Exception` for engine-initiated aborts (an independent exception root — this package is a child root and must not depend on the area contracts; a model engine that surfaces an abort through the area's session contract wraps it in a `DatabaseException` at the model boundary, the same rule the engines apply to `StorageException`); `TransactionDeadlockException : TransactionAbortedException` for deadlock victims (retryable by construction). `TransactionCommitUnconfirmedException : Exception`, a second independent root, for a commit whose record was written but not made durable: the transaction is committed, so it is deliberately not an abort and not retryable ("A commit record that was written but not made durable", above); engines wrap it in the area's `DatabaseTransactionCommitUnconfirmedException`. Caller-initiated rollback is not an error: once started it throws nothing (#1226); before the start it throws only for a canceled token or a context it cannot end, and `ObjectDisposedException` once the manager's disposal began.

The version store throws the storage library's errors unchanged when a version page cannot be read: `PurgeWriterAsync` (a failed undo, which requeues the writer), `PruneAsync` and so `RunVersionPurgePass` (each after reclaiming everything else it can), and `GetVisibleVersionAsync`. A version reclaimed beneath its ledger entry is not one of them (#1342; "Shared per-database composition (#918)", below).

## The engine binding (first adopter: the SQL engine)

The area's recorded *isolation split-brain* — a complete MVCC manager no engine
used — closed with the SQL engine's session binding (area DESIGN.md §3.8;
work items under #862). The integration kept this package exactly as shaped:

- The **model engine session** binds the root's `DatabaseTransaction` to a
  `TransactionContext` from a per-database `TransactionManager` — the binding
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
  durability through journal ordering). `BTreeIndexManagerOptions.TransactionSource` (in
  `Database.Indexing`, a delegate since #1258) is the pairing seam the engine adapts from the shared coordinator —
  resolving a context's current statement bracket. Recovery drives the
  version store's aborted-writer purge from `TransactionRecovery.Analyze` at
  every database open — and `Analyze` reads the active-sequence list out of
  checkpoint records, so classification survives journal truncation beneath
  in-flight transactions.
- The shared `RecordSpaceVersionStore` derives from `VersionStore` over the engine's
  record access adapter (the in-memory store remains for tests and
  embedded working state): row versions live in data pages as stamped records,
  and the store is the *ledger* of each writer's effects, which is what makes
  `PurgeWriterAsync` a physical logical-undo and `PruneAsync` a physical
  space reclamation. `LockManager` arbitrates row-grain write conflicts
  (exclusive locks on row identity, intent locks at table grain for DDL — the
  B+Tree uniqueness precedent generalized), and deadlock victims cross the
  model boundary as the root's `DatabaseTransactionDeadlockException`.
- The engine's version-purge worker drives the reclamation duties on its own
  timer (#910): it retries the undo of rolled-back writers whose inline undo
  failed, releasing each writer once its undo completes (#1226, "Ending a
  transaction" above), and `PruneAsync` below the safe snapshot bound — the minimum
  snapshot floor across active transactions, not `OldestActive` alone, which
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

- `TransactionRecordSpace` supplies read/update/delete and physical location
  packing/unpacking. The storage's existing unit iterator scans the stamped
  records. SQL retains its object-id/column tuple and row APIs; KeyValuePair
  retains its key/value tuple and entry APIs. Their current location encoding is
  `(pageId << 16) | (ushort)slotIndex`, but the ledger treats it as an opaque identity.
- `RecordVersionIndex` supplies stamp-checked erase and clear-deleter operations
  over encoded key bytes. Indexing's `BTreeRecordVersionIndex` adapts `BTreeIndex` to this
  contract, and Documents.Catalog and Graph.Storage supply their own adapters. Indexing
  already references Transactions; the reverse reference would form a cycle. The ledger
  copies keys at registration, keeps index undo in the same physical bracket as record
  undo, and retries the same entries on failure.
- The engine adapts `TryGetStorageTransaction` to the existing Indexing pairing
  seam, retaining its `DatabaseException` when no statement bracket exists.
  Transactions never references the area root or its exceptions.

`RecordSpaceVersionStore` is a ledger over actual records, not a second payload
store. Logical rollback deletes created versions and clears tombstones only
when their current stamps match the recorded writer. A failed physical statement
can leave stale ledger entries; they stay harmless because the store reads a
ledger location with `Storage.TryReadRecord`, which reports a reclaimed location
(a deleted or reverted slot, a freed page, a page reallocated as a non-data page)
as nothing to do, and the stamp rechecks reject a location another writer reused.
Committed tombstones enter the prunable set. Only committed writers enter it, so a
rollback searches the set for the writer's tombstones only when the writer has a
ledger: a transaction that wrote nothing, such as a read-committed statement's
snapshot pin, ends without a scan of every tombstone the purge has not reclaimed
yet, which cost each pin's end time in proportion to that backlog (#1363 review).
Pruning requires
`deleter < safeBound` and rechecks the current deleter before deleting. Index
undo stays in recorded order, including its original accounting semantics.

A version page the store cannot read is not a reclaimed location (#1342): the
undo or the prune throws the storage error (`StorageCorruptionException` for a
page that fails its checksum or is malformed, `StorageIOException`, or the
device's `IOException`). A failed undo requeues the writer for the purge worker,
which keeps it in flight and its versions hidden until a retry completes. A prune
keeps the candidate it cannot read, reclaims every other candidate and commits
their batches, and then throws the first such failure; `RunVersionPurgePass`
likewise attempts every queued writer and runs the prune before it rethrows a
failed undo. One unreadable page therefore costs one tombstone or one writer, not
the whole record space's reclamation: a prune that stopped at the page would roll
back the batch beside it and never reach the ones after it, on every pass. The
failure still reaches the purge worker, which reports it and retries with backoff.
PostgreSQL's `VACUUM` errors out of the relation instead: its heap pass reads each
block through the buffer manager (`src/backend/access/heap/vacuumlazy.c:1401`),
which raises `ERRCODE_DATA_CORRUPTED` for an invalid page
(`src/backend/storage/buffer/bufmgr.c:8889`, `:8913-8919`). The per-candidate split
is affordable here because a prune candidate is one record, not a page to rewrite.
Before #1342 the store read
every `StorageException` as "the slot is gone": an undo that met an unreadable
page forgot the aborted writer with its version still on the page, and once the
page read again every snapshot read the rolled-back write as committed
(`TransactionCoordinatorRollbackTests`, the malformed-page case); a prune
dropped the candidate and left the dead version on its page for good. The
ledger's reads go through the storage the store brackets, not through
`TransactionRecordSpace.Read`: classifying a location needs the page header and
the free-space map, which the record-space seam does not expose, and every
record space reads the same slotted record the storage returns.

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
failure as the cause of an aborted transaction (`CD04101`, `CB04001`, `CG04007`), so a
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
record. A present record shorter than the 16-byte stamp prefix is corruption (#1372),
not an absent version: visibility, logical undo, pruning and recovery scrub report
StorageCorruptionException with page and slot. Failed undo/prune work remains tracked for retry
after repair. Exactly 16 bytes is a valid empty payload. Payload interpretation remains engine
policy; existing format fences refuse earlier formats without a legacy reader or upgrade.
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

During normal operation, writer enrollment, commit/abort appends and changes to the log's
writer set share one monitor with checkpoint capture and truncation. The storage writes the
writer list twice: into a new generation of its file header, made durable before truncation,
and into the checkpoint record appended after it. The header copy survives a lost checkpoint
record and holds any number of sequences (Storage DESIGN.md, "Checkpoints").

**Lazy enrollment and read-only ends (#1280).** Begin still reserves a storage-counter sequence
and inserts the context into the manager's active table under its begin lock, capturing exactly
the same snapshot as before. The journal-bound log's begin call observes cancellation but appends
nothing and tracks no reader. The first admitted apply calls EnterWriter before opening its
physical bracket: it appends one logical begin record and adds the sequence to the writer set.
An apply remains a writer even if its callback makes no changes or its bracket rolls back; this
conservative classification avoids inspecting model mutations. Later applies append no second
logical begin. A commit or abort of a sequence outside that set returns without the log gate: no lifecycle
record and no durable wait. This includes autocommit reads, explicit read-only transactions,
read-committed snapshot pins and readers aborted during disposal. They still end through the
manager, leave the active table and release their locks; visibility and pruning horizons do not
change. A reader cannot report an unconfirmed commit because it appends no commit record. The
engines' offline refusal and the storage sequence allocator's offline check still apply.

**Checkpoint-independent readers (#1269).** Storage reservations use Interlocked rather than
the checkpoint's transaction lock. Sequence assignment, snapshot capture and active-table
insertion still happen together under the manager's begin lock, preserving visibility and
pruning horizons. Writer enrollment is a concurrent membership table whose mutations and
checkpoint enumeration still hold the log gate. End admission has closed and drained every
apply before commit/abort reads that table, so an absent sequence cannot become a writer;
the reader fast path needs no checkpoint gate. A blocked-data-I/O test completes 32 reader
begins, cached pins and mixed commit/rollback ends while both checkpoint gates remain held,
then promotes a reader reserved above the captured header floor and verifies crash recovery.

The physical-file checkpoint latency probe is opt-in via `COHESION_DB_PROBES=1`, using the
same `DatabaseProbeTheoryAttribute` convention as Blob's growth probes; default suites
report it as skipped. Its latency percentiles remain measurements without timing thresholds.

A reader's sequence number may be reissued after a crash: allocation resumes above the
maximum sequence in the surviving journal and the persisted storage sequence floor, which
may not include that reader's reservation. This is harmless because readers stamp no versions
and persist nothing; no durable state can refer to the reissued reader sequence.

Only writers are anchored (#1242). A transaction joins the writer set before its first stamp,
and leaves with its commit or abort record; a rolled-back writer whose undo is deferred stays
until undo completes. A reader spanning a checkpoint has no record or anchor entry. If it later
writes, its first begin record enrolls it under the same monitor, before any bracket or stamp,
so recovery still classifies and scrubs it after a crash. There is no separate active-log or
journaled-reader set to reconcile after a failed checkpoint. The standalone
JournalTransactionLog follows the same rule, enrolled by the manager's apply admission; it does
not own checkpoints. Its internal callers must admit every stamped write and provide any
checkpoint's writer list themselves. Public standalone managers use an in-memory log.

The lock order is apply semaphore, log monitor, storage transaction lock for checkpoint,
header lock, pool flush gate and pool monitor, then journal medium/append locks. Pool flush
I/O releases the monitor, and eviction waits on a frame lease with that monitor released.
Manager and context locks never wait on the checkpoint gates and are released before enrollment. If its begin append
fails, admission is released before the error escapes, so end and disposal can still drain.
Commit appends and removes its writer under the log monitor, then waits for durability outside
it; a checkpoint that truncates first has already durably flushed the outcome. The manager keeps
the writer active until durability completes, or ends it as committed when the flush fails after
the append ("A commit record that was written but not made durable", above).

TransactionCoordinatorRecoveryTests covers writer-only anchors, a reader promoted after a
checkpoint and scrubbed after a crash, and repeated checkpoint/enrollment races until counters
prove the overlap occurred. JournalEnrollmentTests covers standalone read-only ends, locks,
cancellation and refused enrollment. Every model's read-only transaction tests count durable
flush attempts, journal bytes and LSN changes across read loops and every read-only end, including
read-committed pins. TransactionCoordinatorRollbackTests anchors 1,000 writers through a lost
checkpoint record. Its fault allows the anchor initialization image to drain before failing the
post-truncation checkpoint-record write, and asserts the journal is empty while all 1,000 writers
remain anchored. TransactionCoordinatorCheckpointCrashTests cuts power at every whole or torn
write of a checkpoint with 40 writers and a reader. Writer durability and crash classification
keep the same guarantees.

Statement apply, logical undo, and pruning share one semaphore. Engine conflict
locks are acquired before statement apply, keeping genuine lock waits outside
that semaphore and visible to deadlock detection. **The coordinator's checkpoint takes it
too (#1254).** A storage checkpoint runs only while no storage bracket is active, and every
bracket the coordinator opens runs under the semaphore, so `Checkpoint(CancellationToken)`
waits for the statement applying now and runs before the next one is admitted. Without it a
sustained statement load kept a bracket open almost all the time, the engines' checkpoint
workers were refused as busy on nearly every pass, and the journal grew without bound; the
engines' sustained-write tests (a checkpoint truncating the journal on average before it holds
four times a 4 MiB size, over forty sizes written) and the 1.5 GiB SQL measurement (peak 256.4 MiB at the 256 MiB default, `Database.Storage` DESIGN.md,
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
database's gate while another is written past forty sizes, with the written database's journal
truncated on average before it held four sizes, and the held one checkpointed as its statement
ends.

Foreign-storage transaction refusals are operational failures (#1277), including a checkpoint
run by a statement for a deferred request. They are retained for the next worker checkpoint
call; only ordinary storage-bracket contention is retried as busy.

**Maintenance is refused inside a statement apply.** The semaphore is not reentrant, so a
checkpoint or version purge asked for from an `ApplyStatementAsync` callback would wait forever for the semaphore
its own caller holds. The coordinator marks the apply's asynchronous flow (an `AsyncLocal`) and
`Checkpoint`, `TryCheckpoint` and `RunVersionPurgePass` throw `StorageTransactionException`
at entry, before undo or pruning work; before the review the purge call hung with an index pruner.

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

The safe prune bound starts at `max(manager.PruneBound, recoveredSequenceFloor)`
and is reduced to every open context's `Snapshot.Minimum`. A snapshot captured
while an older writer was active can retain a floor below the current oldest
active transaction, so using only the oldest active sequence would reclaim visible data.
`PruneBound` is the minimum of every active sequence and every active snapshot or
serializable transaction's snapshot `Minimum`, read under the manager lock that `BeginAsync` holds while it
assigns the sequence, captures the snapshot and enters the active table, so it covers a
transaction from the moment its snapshot exists. The bound used to start from
`OldestActive` and take snapshot floors only from the coordinator's open contexts, which a
transaction joins after the manager's begin returns. A purge pass in that window pruned
below the new snapshot's floor: the reader then found neither the reclaimed version nor its
successor, whose writer it saw in flight, and read an existing record as absent. An
adversarial probe (six readers looking documents up, one writer, a purge loop, 20 seconds)
read 121 of 5.7 million existing documents as absent with the old bound and none of about
four million, in each of two runs, with this one
(`Prune_SnapshotBegunOnTheManagerAlone_ShouldKeepTheVersionItSees` reproduces the window's
state deterministically). A read-committed transaction adds only its sequence: its
`Snapshot` is captured afresh on every access, so between statements it holds the bound no
further back than itself, as a PostgreSQL backend resets its `xmin` once it holds no snapshot
(`src/backend/utils/time/snapmgr.c:937-955`). A read-committed statement's snapshot keeps the
floor of the moment it was captured, which the manager does not track: a statement view
(`PinStatementSnapshot`) in the Documents, Graph and Blob operations, the snapshot the
statement context captures once in Sql statements and KeyValuePair commands. So every such
statement pins its floor with a snapshot transaction of its own, begun before the capture and
rolled back when the statement ends, on every path. Beginning the pin first is what makes it
sufficient: every sequence active at the capture and older than the pin was already active at
the pin's, so the pin's floor is at or below the statement's, and at or below that of any
snapshot the transaction's own context captures later in the statement. The pin only holds the
bound; the statement does not read through it. A
PostgreSQL backend keeps a running statement's floor the same way: `SnapshotResetXmin` leaves
the backend's `xmin` alone while a snapshot is active and otherwise holds it at the oldest
registered snapshot (`snapmgr.c:941-954`), so the horizon cannot pass a statement still
reading. `BlobReadCommittedTests` holds both halves: the pin
keeps the version while the stream is open, and the next pass reclaims it once the stream
ends, with the read-committed transaction still active.

Sql and KeyValuePair took no pin until #1363, and lost rows exactly this way: a writer that
began before the reader's transaction was in flight when the statement captured its snapshot,
committed while the statement read, and a purge pass then reclaimed the versions it had
tombstoned before the statement reached them. An adversarial probe (one statement per
read-committed transaction from four readers, two writers replacing and deleting rows in
snapshot transactions held open for up to two milliseconds, a purge loop) lost 6,332, 5,406 and
4,278 rows in 3,505, 3,062 and 2,411 of 32,000 to 37,000 Sql statements in three 30-second runs,
all in scans and joins, and 2,149, 2,240 and 1,536 keys in 630, 668 and 434 KeyValuePair scans;
point reads, whose window between capture and read is far shorter, lost one key in about 516,000
and no row. With the purge loop stopped it lost none, and with the pin it lost none in two
30-second runs of each model (about 60,000 Sql statements, 41,000 scans and 331,000 point reads)
(`SqlReadCommittedSnapshotPinTests`, `KeyValueReadCommittedSnapshotPinTests`).

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
(`database-area.md`, "Test doubles"). The coordinator carries four internal hooks, reached
only through this package's `InternalsVisibleTo` grant to its own tests:

| Hook | Where it runs | Used for |
|---|---|---|
| `BeforeCheckpoint(long[] writers)` | under the lifecycle append gate, just before `storage.Checkpoint(writers)`, inside the same `try` | the writers a checkpoint lists, a checkpoint held open against a concurrent lifecycle append, a failed checkpoint |
| `SequenceReserved()` | in the manager's sequence allocator, after the storage reserved the sequence and before the context returns | a begin that reached the coordinator while a checkpoint holds the append gate (the reader may now finish; its first apply waits) |
| `BeforeContextTrackedAsync(TransactionContext context)` | after manager registration, before coordinator tracking, with neither lock held | pause begin while disposal aborts its context and clears the coordinator table (#1351) |
| `BeforeAbortRecord(long sequence)` | inside the `try` of the abort record's append, before the journal append; a throw rejects the record | a lost abort record on a storage that stays online |

Each is `null` unless a test sets it and runs once per checkpoint, sequence reservation,
context begin or abort record, never per row. Each rewritten test asserts that its hook fired: the
`Checkpoint` helper of the rollback tests starts its capture unset and requires it set.

**Stream-level triggers cover the rest.** #1252 made every journal append a write into a
user-space buffer, so a failed journal write now happens at a drain, and a failed drain
takes the storage offline. The tests of an undo whose journal writes fail
(`RollbackAsync_StorageWentOfflineBeforeTheUndo_…` and
`RollbackAsync_UndoRecordsLostAtTheNextDrain_…`, which replaced
`RollbackAsync_UndoBracketJournalWriteFails_…`) fail the journal's backing stream
(`FaultingMemoryStream.FailWrites`) and need no hook. The abort-record hook stays because
no stream-level failure rejects one record and leaves the storage online.

## Diagnostics

The kernel raises its own events through one internal event source, named for the assembly:
`Assimalign.Cohesion.Database.Transactions` (`src/Internal/EventSource/TransactionEventSource.cs`;
catalog: `docs/programs/DATABASE_EVENT_SOURCES_PLAN.md` §4.3). Every engine model composes its
database's kernel through `TransactionCoordinator`, so the one source covers the transactions of
all five engines. The explicit transaction a session runs is the root's
(`Assimalign.Cohesion.Database`); the events here are the kernel transactions: one under each
explicit transaction and one per autocommit statement. In every model, statement snapshot pins
use `BeginSnapshotPinAsync` and end through `RollbackAsync`. Their immutable kind is assigned
under the manager lock before active-table insertion (#1351), preserving the snapshot/prune
ordering described above. Pins remain in the current-transactions gauge but move none of the
three transaction rates and emit none of the Verbose begin, commit or rollback events, including
when disposal aborts them. Real transaction rollbacks and failures still count. The conventions
every Database source shares (failure payloads, `Start`/`Stop` endings) are in the area's
[`DESIGN.md`](../../../../docs/resources/Database/DESIGN.md#diagnostics-one-event-source-per-assembly).

**The `database` payload is the storage's name.** The kernel types do not know their database,
so the coordinator hands `Storage.Name` to the two it builds, through internal members only: the
`TransactionManager` constructor's `database` parameter and `LockManager.EnterEngineMode`. The
events the coordinator writes itself read the name from its storage inside the enabled check. A
standalone `TransactionManager.Create` or `new LockManager()` reports an empty `database`.

**Payloads carry identifiers only** (event-source.md rule 11; plan D8): a sequence, a lock mode,
a lock resource's kind and object id, a storage-offline cause, counts, and, for the kernel's own
device and infrastructure failures (events 4, 5, 11, 13 and 17), an exception's type full name
and `Message`. No record, key or value. **`resource` never carries an entry's id**
(`TransactionEventSource.DescribeResource`): it is `Database`, `Object:<objectId>` or
`Entry:<objectId>`. A unique index's and a KeyValuePair key's entry id is an unseeded FNV-1a hash
of the key (`IndexKey.Hash`), and an unseeded 64-bit hash of a key drawn from a small domain is
the key, since enumerating the domain reverses it. The lock manager cannot tell such an entry from
a Sql row-location entry (its packed page and slot): both are `LockResourceKind.Entry`, and the
key-hash identity is the published pre-acquire contract the Sql executor depends on, so no new
resource kind separates them. Every entry is therefore written as its object; the object id still
locates the table, collection or key space, and a Sql row's location is dropped with the rest.

Keywords: `Transactions = 0x1`, `Locks = 0x2`, `Checkpoints = 0x4`, `Purge = 0x8`; the other
events check `EventKeywords.None`.

| Id | Event | Level | Keyword | Payload | Written by |
| --- | --- | --- | --- | --- | --- |
| 1 | `TransactionBegun` | Verbose | Transactions | `database`, `transactionSequence`, `isolationLevel` | `TransactionCoordinator.BeginAsync`, once the context is tracked |
| 2 | `TransactionCommitted` | Verbose | Transactions | `database`, `transactionSequence` | `TransactionCoordinator.CommitAsync`, after the manager's commit |
| 3 | `TransactionRolledBack` | Verbose | Transactions | `database`, `transactionSequence` | `TransactionCoordinator.RollbackAsync`, after the manager's rollback, also when its undo was deferred |
| 4 | `CommitRecordWriteFailed` | Warning | — | `database`, `transactionSequence`, `exceptionType`, `exceptionMessage` | `TransactionManager.CommitAsync`: the commit record could not be written, so the kernel rolled the transaction back (the caller's failed commit is the root's Error). It pairs with 13; named apart from the root's event 32 `TransactionAborted`, which is an explicit transaction an operation aborted |
| 5 | `CommitUnconfirmed` | Error | — | `database`, `transactionSequence`, `exceptionMessage` (the flush failure's: the inner exception's message) | `TransactionCoordinator.CommitAsync`, on `TransactionCommitUnconfirmedException` |
| 6 | `DeadlockDetected` | Warning | — | `database`, `transactionSequence` (the victim), `resource`, `mode` | `LockManager`, when a request's wait would close a cycle, or when a grant closed one through a request the grantee still had queued, which is then the victim (#1390; the queue's places leave that second case no way to happen with the five modes, and the search stays as a check: "The wait-for graph is read from the table") |
| 7 | `LockWaitStart` | Verbose | Locks | `database`, `transactionSequence`, `resource`, `mode` | `LockManager`, when a request is queued |
| 8 | `LockWaitStop` | Verbose | Locks | `database`, `transactionSequence`, `outcome`, `durationMilliseconds` | `LockManager`, when the queued wait ends, on the waiting flow, only for a wait whose `LockWaitStart` was written (`System.Net.Http`'s `RequestStop` shape); `SlowLockWait` is written either way, so a listener that attached during a long wait still learns how long it lasted |
| 9 | `SlowLockWait` | Warning | — | `database`, `transactionSequence`, `resource`, `mode`, `durationMilliseconds`, `thresholdMilliseconds` | with 8, written before it, when the wait lasted at least the threshold |
| 10 | `LockWaitsAbandoned` | Warning | — | `database`, `cause` (the `StorageOfflineCause` name) | `LockManager.Abandon`, from `AbandonLockWaits`, once per lock manager |
| 11 | `UndoDeferred` | Warning | — | `database`, `transactionSequence`, `exceptionType`, `exceptionMessage` | `TransactionManager`, when an abort's undo failed: the writer keeps its locks until a retry completes it |
| 12 | `DeferredUndoCompleted` | Informational | — | `database`, `transactionSequence`, `undone` | `TransactionManager`, when a retry completed a deferred undo and released the writer |
| 13 | `AbortRecordWriteFailed` | Warning | — | `database`, `transactionSequence`, `exceptionType`, `exceptionMessage` | `TransactionManager`, when the advisory abort record could not be appended (swallowed before this source) |
| 14 | `RecoveryAnalyzed` | Informational | — | `database`, `committed`, `aborted`, `maxSequence` | `TransactionCoordinator.AnalyzeAndScrub`, after the scrub |
| 15 | `CheckpointDeferred` | Verbose | Checkpoints | `database` | `TransactionCoordinator.TryCheckpoint`, when a statement held the apply gate (owner decision 23) |
| 16 | `DeferredCheckpointSkipped` | Verbose | Checkpoints | `database`, `reason` (`BracketOpen`: the request stays; `StorageOffline`: it is dropped) | the deferred checkpoint a statement runs as it ends |
| 17 | `DeferredCheckpointFailed` | Warning | — | `database`, `exceptionType`, `exceptionMessage` | the same, when it failed; the next `TryCheckpoint` throws the failure and its worker reports it (root event 1) |
| 18 | `VersionPurgePass` | Verbose | Purge | `database`, `versionsPurged`, `durationMilliseconds` | `TransactionCoordinator.RunVersionPurgePass`, when the pass ran to its end (a retry failure it rethrows included) |
| 19 | `LockWaitBlocked` | Warning | — | `database`, `transactionSequence`, `resource`, `mode`, `durationMilliseconds`, `blockers` | `LockManager`, once per wait still queued when the slow-lock-wait threshold passes, from a one-shot timer the wait arms only while a listener takes warnings (#1390). `blockers` names each transaction the request waits for: a holder as `<sequence>:<modes held>` (modes joined by `+`), the owner of a conflicting request queued ahead of it as `<sequence>:queued <mode>`. Each is followed by what that transaction has queued itself: `" waiting for <mode> on <resource> (<its blockers>)"`, one level deep, so a cycle of two shows in one report and each waiter of a longer chain reports its own step. Bounded (#1390 review): 16 blockers, 4 queued requests of each and 4 blockers of each of those, the rest counted as `... (+N more)` and ` and N more requests` |

**Lock waits.** Only a request that queues writes 7 and 8; one the table grants at once, one it
refuses and a deadlock victim never wait. 7 and 8 are a `Start`/`Stop` pair on one flow
(event-source.md rule 8): the stop is written in the `finally` around the waiter's await, whatever
ended it, and names how in `outcome`, the one `Stop` of the area whose ending is a domain outcome
rather than the area's `status` (the area `DESIGN.md`): `Granted`; `Cancelled` by the caller's
token; `Timeout`, because the individual lock timer expired; `Ended`, because the owner's transaction ended while it waited; `Deadlock`, because a grant
closed a cycle through it (#1390; since the #1390 review's queue no grant can, and the outcome
stays for the check that would report one); or `Abandoned`, because the storage went offline. The
cause captured when the request leaves the queue drives both this event and its exception. The cancellation
registration is disposed inside that `try`, as soon as the await ends and before the stop is
written, the order the wait had before it was traced. The wait is timed whether or not anyone
listens: it already costs a queued waiter, a completion source and a registration, and a listener
that attaches during a long wait then learns its full length (plan D5 (b)). The deadlock victim's
event is written after the lock table's lock is released, just before the refusal is thrown; a
victim a grant chose has its event written after the lock is released, just before its wait is
failed. `LockWaitBlocked` (19) is written on the timer's thread, after the lock table's lock that
its description was read under is released.

**Events written under a lock.** `LockWaitsAbandoned` (10) is written from the storage's offline
hook, which can run under `Storage`'s transaction lock (a checkpoint or shutdown flush that failed),
as Storage's own `StorageOffline` is; a listener's synchronous work runs there, as was decided for
the buffer pool's events. No other event here is written under the lock table's or the manager's
lock.

**The slow-lock-wait threshold** is an EventSource argument, not API (plan D7): each session that
enables the source sets it to its `SlowLockWaitThresholdMs` argument, or to 1000 ms when it passes
none or a value that is not a finite, non-negative number, as the in-process forwarder does. The
last enabling session wins, and any session's disable restores the 1000 ms default until the next
enable, as the root's `SlowStatementThresholdMs` does: a brief tool session's 0 ms never outlives
it under a forwarder that stays enabled. PostgreSQL logs lock waits
past `deadlock_timeout` (1 s) by default (`log_lock_waits`, `src/backend/storage/lmgr/proc.c:1700`);
the 1000 ms default follows it, pending owner question Q2 of the plan:

```bash
dotnet-trace collect --process-id <pid> \
    --providers "Assimalign.Cohesion.Database.Transactions:0x2:4:SlowLockWaitThresholdMs=250"
```

**Counters.** All six are created on the first enable command; their backing fields are updated
with `Interlocked` on every transition whether or not anyone listens, so a tool that attaches late
reads exact values.

| Counter | Kind | Transition |
| --- | --- | --- |
| `current-transactions` | gauge | +1 when the coordinator tracks a context (`BeginAsync` or `BeginSnapshotPinAsync`, including pins); −1 once per context when it stops: `UntrackEnded`, which only the end that removes the context reaches, or `DisposeAsync`, for each context its disposal still held |
| `transactions-per-second` | rate | a tracked context other than a snapshot pin begins |
| `commits-per-second` | rate | a context other than a snapshot pin ends `Committed` (an unconfirmed commit included) |
| `rollbacks-per-second` | rate | a context other than a snapshot pin ends rolled back or aborted, including an aborted commit or disposal's abort |
| `total-deadlocks` | total | each deadlock victim (event 6) |
| `lock-waits-per-second` | rate | each queued request (event 7) |

The transaction rates exclude snapshot pins; the gauge includes every open context, so it still
exposes a leaked pin. A pin keeps its ordinary manager registration, sequence, fixed snapshot,
end claim and lock release. It writes no journal lifecycle records (#1280); registered statement
snapshots without kernel contexts are outside #1351. The lock counters count every lock manager.
The transaction rates move once at a non-pin kernel transaction's begin and once at its end,
which is once per autocommit statement (plan D6 reads statement throughput from
`transactions-per-second`), beside a per-database lock; only writers append journal records
(#1280). No counter moves per row or per frame. The six backing fields are process-global and sit
together, so the begin and end `Interlocked` operations of every database in the process share
them (plan D6 sanctions these counters).

**A begin racing the coordinator's disposal.** `BeginAsync` tracks the context after the
manager's begin returns. The coordinator's disposal aborts every active context and then clears
its table, counting each one out of the gauge. A context the manager's disposal aborted before
`BeginAsync` added it would be added after that clearing and never counted out, so `BeginAsync`
runs `UntrackEnded` right after adding it: a context that is no longer active leaves the gauge
there, and one the later clearing counts is no longer in the table when `UntrackEnded` looks. No
timing assumption is needed: the internal `BeforeContextTrackedAsync` hook pauses begin after
manager registration, and a non-parallel test completes disposal before resuming tracking. The
test requires one observed crossing of that window; its timeout is only a hang ceiling. The
gauge must return and all rates stay fixed. Cancellation before begin, a refused
second end, a canceled rollback followed by disposal, and each successful end path are covered
by exact counter deltas. Each model retains and sums every published increment for read hits
and misses through publications after the reads finish, then verifies that its real explicit
rollback still counts. The canonical recorder is wrapped by a test-side rate history.

**Cost when nobody listens** (event-source.md rule 9): every write is behind
`IsEnabled(level, keywords)`, and every `ToString` and the storage name read are inside it. An
uncontended `LockManager.AcquireAsync` reaches no instrumentation before its grant. Two
`TransactionEventSourceTests` delta tests pin it. The engine-mode path every engine uses never
enters `AcquireCoreAsync`: a re-grant allocates 0 bytes, and a new resource allocates exactly what
the `TryAcquire` it runs does. The standalone path (`new LockManager()`) runs `AcquireCoreAsync`,
the method the lock-wait and deadlock events are written from, on every request, and in an
optimized build allocates nothing for it. Until #1390 it allocated the 56-byte closure of its
cancellation registration per request, which the compiler created at the method's entry because
the closure captured a parameter; the callback is static now, and the queued waiter its state
(Release, `19b1a495` and #1390: 56 and 0 bytes per uncontended standalone request, 0 and 0 in
engine mode). A Debug build also allocates the async state machine, which the compiler emits as a
class there, so the test checks the bound only in an optimized build; CI tests Release. A wait
arms the `LockWaitBlocked` timer only while a listener takes the source's warnings.
`RunVersionPurgePass` takes its timestamp only while a listener takes event 18.

## Non-goals

- No distributed transactions / two-phase commit — single-node ACID first.
- No lock escalation policy in the contract — an implementation concern.
- No savepoints in the MVP surface — add to `TransactionContext` when a model needs them.

## AOT posture

Static composition, contracts and value objects; `FrozenSet<ulong>` for the active
set. The shared composition and engine adapters use ordinary calls and BCL
synchronization. No reflection, dynamic code generation, or `Microsoft.Extensions.*`.
The event source writes only strings, integers and doubles, which bind to the trim-safe
`WriteEvent` overloads; a NativeAOT application receives its events only with
`<EventSourceSupport>true</EventSourceSupport>`, and nothing depends on delivery.

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

Reference consulted: RavenDB test/StressTests/Voron/InvalidReleasesOfScratchPages.cs:39–68 and :71–103 use LongRunning stress drivers. No code was copied.

## Index pruning in the version-purge pass (#1195)

The existing three-argument coordinator constructor remains. The new overload is
`TransactionCoordinator(Storage storage, StorageJournal journal, TransactionRecordSpace records,
Func<TransactionSequence, CancellationToken, ValueTask<long>> indexPruner)`. It retains the
non-null callback for the database lifetime. `TransactionSequence PruneBound { get; }` exposes
the existing conservative bound, including manager snapshot minima, the recovered floor and
open coordinator contexts, for Indexing's opportunistic unique-insert walk. Computing it visits
open contexts under the coordinator lock rather than allocating a context-array copy; the manager
lock never waits for coordinator apply/log gates, preserving the existing lock order.

`RunVersionPurgePass` first retries aborted undo and prunes records, then takes the apply gate
and invokes the index callback with that same safe bound and cancellation token. A bounded
Indexing callback visits at most 64 leaf pages. SQL, KeyValuePair, Documents and Graph all bind
their current manager; catalog/store reattachment after a failed DDL does not leave a stale
captured manager. Blob has no B-tree manager. Scheduling stays on the existing version-purge
worker; no model owns a second vacuum or reaches into tree pages.

The returned count and existing VersionPurgePass event's reclaimed total now include physical
index entries. No new event/counter, interface or format is added. A recoverable record-prune
failure still allows independent index work; the first deferred failure is rethrown unchanged
after the pass. Cancellation, disposal and out-of-memory failures escape immediately. The apply
gate is released for every callback outcome, including asynchronous ValueTask failure. Providers
must perform only bounded index maintenance and must not request a checkpoint inside this gate.
Tests prove gate exclusion, exact exception/cancellation identity, release after failure, old
snapshot preservation and the four real model bindings. Existing statement rollback semantics
are unchanged, as approved in #1393.

## Confirmed record decoding (D1, #1372)

The record-space version store rejects a present record shorter than
RecordVersionStamp.HeaderSize in visibility, undo/pruning stamp reads and recovery scrub.
Reclaimed locations remain absent; short present records cannot silently settle ledger work.
The shared StorageCorruptionException carries the physical page, slot and existing CK06003.

Reference consulted, without copying code: Neo4j
community/record-storage-engine/src/main/java/org/neo4j/kernel/impl/store/CommonAbstractStore.java:785–792
retries the page cursor before checking decoding errors; :861–865 checks bounds and cursor errors.
Its record/RecordLoad.java:155–163 retains the cursor failure as the invalid-record cause.
Cohesion uses the already-established D1 seqlock rather than adding a page read latch.

Stable malformed copies are reported without speculative retries or waiting for a whole
physical transaction. The existing storage read confirms both bytes and absence against
all page mutations (#1365), including the copy-to-confirm window. Storage I/O, cancellation,
timeout and existing storage exception identities are not caught by decoder-only wrappers.
No file format, version, protocol frame, event, counter or diagnostic number changes.

## #1420 measurements

Release Windows ARM64 / .NET 10.0.13, tiering and background GC disabled in the standalone
measurement host, shared machine. The committed #1390 probe runs one warm-up and twelve
interleaved five-second samples per shape on each version. Precise active durations normalize
commits/victims; setup-only reflection invokes the existing test helper outside its worker hot
loops. These are development-scale measurements, not rankings or per-sample timing assertions.
The before DLLs are frozen, and loaded SQL/Transactions/Storage hashes are checked on the fixed
operation probe. No unexpected failure or wait of ten seconds was recorded in either phase.

| Shape | Median commits/s before -> after | Median victims/s before -> after | Median victims / 1,000 commits before -> after | After samples above before commit-rate median |
| --- | --- | --- | --- | --- |
| 6 parents, 3 deleters, 5 writers | 899.412 -> 1440.178 | 17.825 -> 23.169 | 20.234 -> 16.419 | 12/12 |
| 1 parent, 1 deleter, 14 explicit writers | 246.993 -> 355.118 | 28.369 -> 27.155 | 115.068 -> 77.325 | 12/12 |

Absolute default-shape victims increase from 1,095 to 1,401 while more work commits; its parent
deletes increase from 8,261 to 12,693. Hot-shape victims decrease from 1,752 to 1,613 and parent
deletes increase from zero to 24, but only four of twelve samples make parent-delete progress.
Its median parent-delete rate remains zero. D2 therefore improves this measured shape without
guaranteeing uniform cascade progress. Earlier grants and retained wait grants can still form
hard cycles; this result does not authorize key-share modes or a different victim policy.

A separate fixed operation deletes one parent with 32 children and rolls back outside the
measured interval. After one warm-up, twelve batches of 100 deletes measure median
80,958.5 -> 81,858.5 ns/delete and 108,032 -> 108,056 B/delete. Four of twelve final duration
samples are at or below the old median. Allocation is the precise process-wide GC counter,
including engine workers; periodic worker samples are visible in the raw data. Every batch
validates the restored child ids/parent values outside timing. Digest FBF326266BA187D1 is
unchanged. The additional 24 median bytes are on DELETE; no context/metrics field was added
to the SELECT path. Stored-query allocation remains a required Release guard.

Raw measurements/hashes/harness: temporary wave11-1420-probe-before/after.jsonl,
wave11-1420-delete-before-v2/after.csv, wave11-1420-delete-final-hashes.json and
Wave11CascadeProbe/Wave11DeleteProbe. The initial delete baseline without the final payload
verification is retained separately; the figures above use the final matching harness on both
versions. The local Windows results do not replace official quiet-host profiling.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.
`LockManager()` is public; the parameterless `Create()` factory is removed. FIFO grant order, statement marks and deadlock handling are unchanged.

## Debugger inspection convention

Transaction debugger views filter private/internal coordination state. Reviewed public identity, sequence, isolation and state values may be projected; snapshot capture, prune-bound work, oldest-active scans, lock-taking counts and retry-clock/provider calls are omitted. The views do not enter a transaction, acquire a lock, refresh a snapshot or expose internal-only counters.

Concrete eligible types carry their own concise display and private nested DebugView at the end of the type; an effective inherited display/proxy pair is reused. Projections are public read-only members of that private view and never reveal private/internal data. Empty views use the constant type name. Debugger attributes and nested infrastructure add no public API, instance fields to existing runtime types, storage/wire format change or successful-path work.
