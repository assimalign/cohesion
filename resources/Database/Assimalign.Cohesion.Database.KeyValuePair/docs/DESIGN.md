# Assimalign.Cohesion.Database.KeyValuePair — Design

## Wrapped child failures and abort evidence (#1364)

Otherwise unclassified `StorageException` subclasses and ordinary I/O, including wrapped
and aggregate causes, use `COHDBK005`. Offline and format refusals retain their specific codes.
Attached-index damage leads with `COHDBS003` and retains `COHDBI002` and `COHDBK005`;
other index failures use `COHDBK005`. Begin, command, commit and rollback share classification.
Create and open also code child failures before publication. Known format refusals retain their
authored messages, source diagnostics and inner causes. A failed command aborts an explicit
transaction after releasing admission. If that abort also fails, the exact coded exception
remains primary, including its corruption type; the model EventSource records the cleanup
failure as `TransactionCleanupFailed`. Transaction refusals retain their own code. Real
device-read regressions cover slot, journal and storage-transaction failures through both
wrapper shapes. A failed deferred-undo notification proves an abort cannot replace the read
diagnosis or its exception type.


## Wire contract ownership

`Assimalign.Cohesion.Database.KeyValuePair.Protocol` owns this model's wire messages, codecs, family singleton and transferred value shapes (5 public types). Both engine and remote client reference that one assembly. Its public namespace and RootNamespace equal its project name. Engine requests, planning, catalog, storage and server lifecycle stay in `Assimalign.Cohesion.Database.KeyValuePair`. This extraction preserves wire bytes and validation; moved public types intentionally change namespace and assembly identity with no forwarders.


## Model component boundaries

Catalog, Storage and the empty Security satellite projects are consolidated into Assimalign.Cohesion.Database.KeyValuePair. Public Catalog and Storage types keep their existing namespaces and live under src/Catalog and src/Storage; their assembly identity is now Assimalign.Cohesion.Database.KeyValuePair. Internal component types live under src/Internal/Catalog and src/Internal/Storage and declare Assimalign.Cohesion.Database.KeyValuePair.Internal.

Engine-owned storage strategies, row/entry codecs and catalog-aware persistence glue live in src/Internal/Engine/Storage; the architecture test enforces strategy placement there. TransactionRecordSpace implementations and physical location-packing helpers belong to the Storage layer (src/Internal/Storage, or nested within a public Storage type); adapter placement is also enforced. This refines the original fold folder map without changing namespaces, members or behavior.

The dependency direction remains engine → Catalog → Storage → shared kernel. Storage-folder types must reference neither Catalog-folder nor engine types; Catalog-folder types must reference no engine types. The build exports the actual compiler inputs, and this model's architecture test recompiles them with Roslyn and checks resolved type/member symbols by their declaring source folders, including partial and generated types. Referenced types also pass a per-layer containing-assembly allowlist seeded from the retired satellite csprojs at a64426436: Storage permits shared Storage, Transactions, Indexing and the actual BCL reference pack; Catalog additionally permits the area root and Types. Engine code is unrestricted. See the area README for the common placement and assembly rules. Build-generated resource accessors and assembly metadata are shared infrastructure, outside the three layers. No new shipped-assembly visibility grant, wire or disk format, diagnostic code, or behavior is introduced.


The key-value engine (area architecture:
[docs/resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.3, generality report §3.10):
an ordered key space over the shared kernel, and the **second model engine** —
built deliberately as the proof that the kernel is model-general, not SQL-shaped.

## String comparison and collation (#1025)

Key-Value remains binary-only for user data: keys and values are opaque bytes,
and key equality, ordering, uniqueness, and prefix ranges use unsigned
lexicographic byte comparison. Text supplied as a key is compared in its encoded
form without case or accent folding. Command keywords and administrative database
lookup remain ordinal-ignore-case; those identifier rules do not transform user
keys. SQL database defaults and column/expression `COLLATE` have no effect on
this model. Configurable text collation is deferred.

## Design intent

Compose kernel pieces, never re-implement them — and compose them in a
*different shape* than the SQL engine, because the difference is the point:

| | SQL engine | Key-value engine |
|---|---|---|
| Primary structure | the record space (scan-primary; indexes are secondary accelerators) | **the B+Tree primary key index** (index-primary; every read is a seek) |
| Record payload | object-id-prefixed typed tuple, schema from the catalog | key + value as two binary tuple components, self-describing |
| Conflict grain | table intent locks + per-row location locks + unique-key locks | **key locks only** (one per command) |
| Statement surface | the SQL dialect | data commands plus `KEYSPACES` discovery (docs/COMMANDS.md) |
| Catalog | schemas/tables/columns/indexes | registrations + format marker only |

Both engines share, unchanged: the storage substrate (slotted pages, per-owner
chains, WAL v2, recovery), the MVCC discipline (16-byte writer/deleter stamp
prefix, snapshot visibility, first-updater-wins latest-state checks, logical
rollback via the version-store ledger, open-time recovery scrub), the B+Tree
(MVCC-stamped entries, latest-state uniqueness under hashed-key locks), the
one-sequence-namespace pairing, and the per-statement bracket/apply-gate model.

## Execution model

Automatic commit follows the same cancellation boundary as every other engine (#1246):
once command execution has completed, the session ends its context without forwarding the
caller token. A cancellation during that commit cannot change success into an abort; real
append and durability failures retain their existing outcomes. Explicit commit and rollback
observe cancellation only before their shared end starts. See the Transactions DESIGN,
"Ending a transaction", for the kernel rule.
`PutAsync` consumes its already materialized outcome without the command token after
execution completes, so cancellation during automatic commit cannot mask a committed write.

The execution-completion case in `KeyValueCommitCancellationTests` cancels inside the PUT
apply callback after its final effect, before coordinator/log commit entry. It proves the
session boundary through success, affected count and persistence across reopen. The late-flush
and explicit-transaction cases are regression guards, not proof of that session fix.

- **Index-primary reads.** `GET`/`EXISTS` seek the unique primary index
  (`key` → packed record location) through the command's snapshot; `SCAN` drives
  an incremental snapshot cursor over an `IndexKeyRange` (#1542). Open and each move receive the command token; LIMIT stops after its bounded batch, and disposal drops owned keys. Reads finish before any mutation applies. Keys go into `IndexKey` **raw** —
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
  integrity checks are self-describing. The entry-space format version is
  catalog-persisted and gated at open (below); there is no upgrade machinery.
- **Entry-space format 2 (#1194), gated in both directions.** The marker
  describes the whole data file set: the entry records and the primary index
  tree that rides it. Format 2 keeps format 1's entry records, but its primary
  index is a tree of `Database.Indexing`'s B-tree page format 2 (entries ordered
  by key, entry location and writer) instead of format 1 (ordered by key alone).
  The engine stamps the marker at creation before it registers the primary
  index, and at open, before it attaches the index or recovery writes anything,
  it refuses any database that registers a primary index on a marker other than
  2 — "Database 'x' uses entry-space format 1, but this engine supports only
  format 2. …" with the export, drop and recreate remedy, or the newer-engine
  remedy for a higher marker. (A catalog that registers no primary index is a
  creation interrupted before the registration; nothing was written through it,
  so the open bootstraps the index and stamps 2.) The bump also fences the other
  direction: an engine before #1194 rejects a marker newer than its own 1, so it
  refuses a format-2 database before it attaches the tree instead of misreading
  it. **Behind the marker, the index manager checks the tree itself**: it reads
  the root page's B-tree page format when the instance attaches the
  registration, and a tree the marker does not describe is refused with
  `DatabaseException` "Database 'x' cannot be opened. COHDBI001: Index … uses
  B-tree page format 1, but this engine supports only format 2 …", the index
  manager's `IndexFormatException` as its inner exception. A cleanly closed
  database is left byte-identical by either refusal; a crashed one has had only
  the storage layer's format-agnostic journal redo and undo, and keeps its
  journal for the engine that wrote it (`KeyValueEngineLifecycleTests` pins both
  refusals). There is no upgrade path (owner decision of 2026-10-02; #1152).
  **Below both, each file set has its own storage format (#1251):** the storage refuses
  a data or catalog file set in another storage format with `StorageFormatException`
  (`COHDBS001`) before its journal is read, and the engine names the database and the
  file set: `DatabaseException` "Database 'x' cannot be opened: its catalog file set
  'x/catalog' was refused. COHDBS001: …" (or its data file set 'x'), the files left
  byte-identical (`KeyValueEngineLifecycleTests`).
  With entries ordered by location, a PUT's tombstone of the key's previous
  version descends to it instead of walking the key's dead versions; the unique
  check still reads them until version pruning (#1195).
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
  `Database.Transactions.TransactionCoordinator` (manager + lock manager + record-space
  version store + gated journal-bound log, one sequence namespace with storage),
  explicit transactions and auto-commit both ride manager contexts, `Snapshot`
  default / `ReadCommitted` per-command refresh (each command captures its snapshot
  after the session begins a snapshot pin that holds that capture's floor until the
  command ends; see
  [Read-committed command pins](#read-committed-command-pins-1363)) / `Serializable` rejected,
  rollback is logical through the ledger, recovery classifies + scrubs record
  space and primary index at open. Kernel aborts are wrapped in the root's
  exceptions at the session boundary (the area error policy). A failed command
  leaves an explicit transaction active; see
  [Failed commands in explicit transactions](#failed-commands-in-explicit-transactions-1225).
- **Result shapes.** `GET`/`SCAN` return result sets (`key`, `value`, `etag`);
  `PUT` a one-row outcome set (`applied`, `etag`); `EXISTS` a one-row boolean
  set; `DELETE` a plain result with its affected count. These shapes ride the
  wire's generic ResultHeader/Row/Complete framing untouched — a deliberate
  constraint so the model needs no protocol surface of its own.

## Failed commands in explicit transactions (#1225)

The #1225 audit asked whether this engine shares the Documents and Blob defect, where a failed
statement rolled the explicit transaction back underneath its caller so that later statements
silently autocommitted and `RollbackAsync` threw. For failed commands it does not, and it keeps
a different contract from Graph, Documents and Blob, deliberately:

- **A command is statement-atomic.** Its writes (the old version's tombstone, the new record,
  both primary-index entries) share one physical bracket that the coordinator rolls back
  physically when the command fails (`TransactionCoordinator.ApplyStatementAsync`), and the
  ledger entries the failed bracket recorded are stamp-verified no-ops at undo and prune. Lock
  waits, latest-version checks and conditional decisions run before the bracket. So a failed
  command writes nothing, and the explicit transaction stays `Active` with every earlier command's
  work intact: later commands run inside it, and `RollbackAsync` undoes all of them. This is the
  SQL session's contract (a failed statement writes nothing and the transaction stays open),
  which the Graph design names as the one it would keep if its storage could undo one statement.
  It holds for every failure: a grammar violation, a first-updater-wins conflict, a unique-index
  conflict found inside the bracket, a deadlock victim, a canceled lock wait. A conflict still
  surfaces as the retryable `DatabaseTransactionAbortedException`; under Snapshot isolation a
  retry inside the same transaction meets the same conflict, so the caller rolls back and retries
  the transaction, as the MVCC tests do.
- **The transaction's own end follows the #1188 contract**, with the code `COHDBK001`, and since
  the concrete-types plan's phase 4 (#1260) the state machine is the root `DatabaseTransaction`'s
  (below, "Concrete types"): the model supplies only its kernel calls, its exception translation,
  its offline refusal and its `COHDBK001` error. A
  transaction that did not commit accepts any number of rollbacks (a catch-block rollback after a
  kernel-aborted commit raises nothing); a committed one refuses a rollback. A commit or rollback
  observes its cancellation token only before it starts, and one that started runs to completion:
  until #1225 a canceled commit token became a kernel abort ("the commit record could not be made
  durable"), and a canceled or failed rollback left the context active, usable and queued for the
  version-purge worker, which would undo work the session went on writing in it. A started
  rollback also always ends the transaction (#1226, [Transactions DESIGN.md](../../Assimalign.Cohesion.Database.Transactions/docs/DESIGN.md#ending-a-transaction-a-started-rollback-always-completes-1226)):
  the kernel ignores a lost abort record, and when the undo itself fails it ends the
  transaction but keeps the key locks of what it wrote until the version-purge pass completes
  the undo. So the end-failure state #1225 first gave this engine, where a commit or rollback
  that failed with the context still active left the transaction `Faulted` and refusing work
  with `COHDBK001` until a later rollback completed, is gone, as it is in Graph. The kernel still
  refuses a rollback before it starts while the database closes (`ObjectDisposedException`: the
  manager's disposal flags itself before it claims any end); the session then refuses commands
  in the ended transaction ("The session's transaction is being committed or rolled back; start
  the operation after it ends."), another `RollbackAsync` fails the
  same way while the close runs and is accepted once the close's abort ended the context, and a
  `CommitAsync` commits nothing: it fails with `ObjectDisposedException` while the database
  closes, or reports the `Faulted` state once disposal's abort ended the context. A commit whose
  record was written but could not be made durable throws
  `DatabaseTransactionCommitUnconfirmedException` and leaves the transaction `Committed`
  (`Database.Transactions` DESIGN.md). Its message leads with the model's code on every path,
  the explicit commit and an autocommit command's own commit alike (owner decision 24 of
  2026-10-06, #1272): `COHDBK002: Database '{name}' went offline while a transaction was
  committing: ...`, built by `KeyValueDatabase.CreateUnconfirmedCommit` through the root's
  `DatabaseTransactionCommitUnconfirmedException.Create(code, database, cause)`, with the kernel's
  `TransactionCommitUnconfirmedException` as its inner exception; before #1272 these paths carried
  the kernel's message alone. A transaction the kernel ended under its caller (disposal's abort, or a
  commit the kernel aborted) reports `Faulted` and refuses commands (typed and text, the text
  before it is parsed) and BEGIN with `COHDBK001` until the caller ends it. `CurrentTransaction`
  returns the transaction until the caller ends it, and null after a commit, rollback or disposal
  (it used to return the ended transaction). Disposing the session rolls back an open
  transaction, and a commit of that transaction afterwards fails with `COHDBK001` naming the
  closure ("The session closed before the transaction ended.").
- **A rollback leaves no write behind, even under a running command.** The transaction can end on
  another thread while one of its commands runs: the caller's rollback, the session closing, or a
  host rolling back a wire session's transaction while a wire command waits for a key lock. Until
  the #1225 review, a `PUT` or `DELETE` parked on the key lock was granted the lock later, after
  its transaction had ended; it then applied its write under the ended sequence, which every
  snapshot reads as committed, so a rolled-back write became visible, and the key lock stayed
  granted to a transaction that would never release it, so every later writer of the key waited
  until restart. Now three rules close it. The kernel admits no bracket of a transaction whose
  end has begun ([Transactions DESIGN.md](../../Assimalign.Cohesion.Database.Transactions/docs/DESIGN.md#ending-a-transaction-under-a-running-statement)).
  The end fails the transaction's queued lock requests, so the parked command fails at once with
  `DatabaseTransactionAbortedException`, even when the kernel defers the rollback's undo and the
  transaction keeps the locks it was granted. And a request queued just after the end is checked once
  the grant arrives: the executor releases a grant made to an ended transaction and fails the
  command, as the Graph, Documents and Blob engines do for their writer lock; while the kernel
  still tracks the transaction (its undo deferred), the coordinator's lock manager leaves that
  release to the transaction manager, which makes it once the undo completes. A grant to a
  transaction that is still active is kept when the command then fails, because releasing all of
  the transaction's locks would expose the keys its earlier commands wrote.
- **A commit waits for no command.** A commit that starts while a command of the transaction is
  still running is refused with a plain `DatabaseException` ("An operation of the transaction is
  still running; commit after it completes.") and leaves the transaction active,
  as Documents and Blob refuse a commit while an operation or stream is open: the command's
  bracket would otherwise race the commit record. A rollback is never refused this way.
- **Over the wire** the protocol has no transaction control, so a host opens the transaction on
  the engine session of one of `KeyValueDatabaseServer.Sessions` (`DatabaseServerSession.DatabaseSession`).
  A failed wire command reports `ParseFailure` or
  `ExecutionFailure`, keeps the session ready and keeps the transaction, exactly as in process
  (`KeyValueTransactionFailureWireTests`, `KeyValueTransactionFailureClientTests`). A connection
  that ends disposes its engine session and so rolls the transaction back; the host's rollback
  afterwards, or racing the teardown, raises nothing, and its commit afterwards fails with
  `COHDBK001`. A host rollback while a wire command waits for a key lock fails that command with
  `ExecutionFailure`, and nothing of it is written.

`KeyValueTransactionFailureTests` covers the in-process cases, and `KeyValueLifecycleTests` the
transaction ending under a running command.

## Read-committed command pins (#1363)

A command in a `ReadCommitted` explicit transaction reads through the snapshot
`KeyValueStatementContext` captures once when it starts. That snapshot's floor can sit below every
active sequence: a writer that began before the reader's transaction was still in flight at the
capture, so the command must keep seeing the versions that writer tombstones. The transaction
manager's prune bound counts a read-committed transaction only at its own sequence
([Transactions DESIGN.md](../../Assimalign.Cohesion.Database.Transactions/docs/DESIGN.md), the
prune bound), so once that writer committed, nothing held the bound at the command's floor, and
a purge pass reclaimed those versions while the command still had to read them:

- a `SCAN` skipped the keys behind them;
- a `PUT` or `DELETE` resolves its key through that snapshot after it waited for the key's lock,
  so it found no version of a key that exists: a `DELETE` reported nothing to delete instead of
  the older writer's change as a conflict (first-updater-wins), and a `PUT` conditional on an
  etag reported a miss.

`KeyValueDatabaseSession` therefore begins a snapshot transaction on the coordinator before it
builds the command context, and rolls it back when the command ends, on every path: completion,
a failed command, a conflict or deadlock, cancellation. Beginning the pin first makes its floor
at or below the command's. The Documents, Graph and Blob operations and the Sql statements pin
the same way; a snapshot transaction, and an auto-commit command (which runs at `Snapshot`),
needs no pin, because the manager tracks its fixed snapshot. On an offline database the pin is
left to the reopen's recovery. The pin only holds the bound: the command still runs under the
transaction's own context, not a `TransactionContext.PinStatementSnapshot` view, as a Sql
statement does (the Sql DESIGN.md's read-committed statement pins say why a view breaks a Sql
cascade's index deletes).

The probe that found it ran one command per read-committed transaction from four readers (a full
scan of 400 keys, or eight point reads), two writers replacing and deleting keys in snapshot
transactions held open for up to two milliseconds, and a purge loop. Three 30-second runs lost
2,149, 2,240 and 1,536 keys in 630, 668 and 434 of 17,000 to 30,000 scans per run; point reads,
whose window between capture and read is far shorter, lost one key in about 516,000. With the
purge loop stopped it lost none; with the pin it lost none in two 30-second runs (about 41,000
scans and 331,000 point reads).
`KeyValueReadCommittedSnapshotPinTests` keeps the probe as a two-second guard
(`COHESION_DATABASE_RC_PROBE_SECONDS` lengthens it) and reproduces the race deterministically with
the `DELETE` above, held at the key's lock while the older writer commits and a purge pass runs.

The pin costs a kernel begin and an abort record per read-committed command: about 1.5 to 2 µs
on a `GET` of one key on the in-memory engine (Release, one process per run at high priority on
four pinned cores, four alternating pairs, median of seven rounds of 20,000 reads; base 1.4 to
3.2 µs, with the pin 3.2 to 5.3 µs). The first cut's pin also cost time in proportion to the
database's history, because its rollback scanned every committed tombstone the purge had not
reclaimed yet and the lock manager's release walked its whole table. With one earlier
transaction's 50,000 or 200,000 deleted keys still unreclaimed, a read-committed `GET` took 60
and 265 µs. A rollback now searches the tombstones only for a writer with a ledger, and a
release visits only the owner's own locks
([Transactions DESIGN.md](../../Assimalign.Cohesion.Database.Transactions/docs/DESIGN.md), the
version store and the lock manager), so the same `GET` takes 3.5 and 3.2 µs; an auto-commit
`GET`, which paid the lock-table walk before #1363 too (38 and 158 µs), takes 2.9 and 2.1 µs.
Each pin uses `TransactionCoordinator.BeginSnapshotPinAsync` (#1351), retaining the same
kernel context, snapshot horizon and release paths. It remains in the current-transactions
gauge but contributes no begin, commit or rollback rate and emits no Verbose kernel lifecycle
events, including during disposal. Read-only pins append no lifecycle records (#1280).
Statement-snapshot registration without kernel contexts remains a follow-up.

## Typed execution and the wire command grammar

`KeyValueDatabaseSession` derives from `DatabaseExecutionSession<KeyValueRequest>`.
It accepts typed commands only; the request root has no statement or parameter map.
The server's internal `ExecuteCommandTextAsync` entry parses the grammar in
[COMMANDS.md](COMMANDS.md). It checks closed session, blank text, cancellation, offline
database and refusing transaction before parsing, then calls the public typed NVI.
The server decodes parameter values before entering it.

The owner decision of 2026-10-16 (#1576) removes the public text seam because the grammar
is deliberately not a language and per-model servers already own the wire path. Execute
frames, command grammar, result framing and error codes are unchanged. Root statement
events name the parsed verb request type; parse failures use this model's event 10, and
pre-parse refusals produce no root statement event.

The 2026-07-14 choice of a minimal grammar reused Execute frames without adding binary
command frames. That remains the wire contract: parser, COMMANDS.md and corpus tests change
together. Model-specific binary frames remain a measured-need optimization.

## Key-space catalog introspection (C2)

The survey found one implicit key space and no named key-space registry. The
catalog holds its primary index registration and entry-space format version;
`GetAsync`, `ExistsAsync`, and `ScanAsync` already expose entry access, but none
describes that key space. `KEYSPACES` extends the existing command vocabulary
with discovery through the session and wire protocol. Its typed counterpart is
`KeyValueKeySpacesRequest`; no existing public interface changes.

The command returns one row describing the catalog-registered implicit space:
database name, key-space object id, entry format version, primary-index name,
index kind, and uniqueness. The exact column order and types are specified in
[COMMANDS.md](COMMANDS.md#key-space-discovery-c2). The key-space id is local to
the database and does not imply named-space support. Physical index pages remain
internal. This model has no compiled-schema ownership or ownership enforcement,
so there is no `OWNER` or `OWNING_SCHEMA` field to report. A smaller surface
faithfully describes its catalog without inventing relational or schema concepts.

The executor captures format and index registrations together under the
catalog's metadata lock when each command runs. Rows are computed in memory from
that capture and never persisted into entry storage or a second metadata cache.
The next command sees newly published catalog state, even inside a snapshot
transaction: catalog publications are self-committing, separate from entry MVCC.
An already returned result retains its capture. The executor receives only its
session's database catalog and name; no selector can address another database.

The catalog snapshot belongs to the catalog package. The executor obtains a
`KeyValueCatalogSnapshot` from the sealed catalog's instance method
`KeyValueCatalog.CaptureSnapshot()` (until phase 4 of the concrete-types plan, a
`public static` bridge over the catalog interface). The snapshot is a sealed class
with an internal constructor; it owns no storage handle and requires no disposal.
The command executor returns the ordinary wire result shape:

```mermaid
flowchart LR
    Wire["KeyValueDatabaseServerSession"] --> Entry["Internal ExecuteCommandTextAsync: validation and refusals"]
    Entry --> Parser["KeyValueCommandParser"]
    Parser --> Session["KeyValueDatabaseSession.ExecuteAsync: typed public NVI"]
    Session --> Executor["KeyValueOperationExecutor"]
    Executor --> Snapshot["KeyValuePair.Catalog snapshot"]
```

`KEYSPACES` is read-only. Both supported mutation verbs reject the reserved
target (`PUT KEYSPACES ...`, `DELETE KEYSPACES ...`) before execution with
`DatabaseParseException`, mapped to `ParseFailure` on the wire, and the stable
diagnostic `The KEYSPACES catalog surface is read-only.` Keys passed as byte
parameters remain data, including the bytes `KEYSPACES`. Scope, fresh captures,
unchanged user storage, and client discovery/refusal are covered by
`KeyValueIntrospectionTests` without replacing existing entry-access tests.

## The key-value server runtime (`KeyValueDatabaseServer`)

The model ships its own wire-protocol server — the **second model server**, the
one whose construction fired the area's recorded server-core extraction trigger
(2026-07-14) and thereby produced the evidence behind the settled placement.
`KeyValueDatabaseServer` is a sealed leaf of the area root's
`DatabaseServer` base fronting exactly one `KeyValueDatabaseEngine`
(`Create(engine, options)`, options in `KeyValueDatabaseServerOptions`), and
this package carries its **own full copy of the server machinery** — accept
loop, session state machine and frame pump (`Internal/`), auth/idle/session
guardrails, two-phase drain. **Per-model duplication is the owner's decision
(2026-07-14), made with this model's extraction evidence in hand:** the
extraction into a shared `Database.Server` was executed and then reversed on
review — model independence outweighs the duplication/drift cost, and
wire-behavior parity is held by the protocol contract plus each model's E2E
suite, not by shared code (the preserved prediction-vs-evidence table and the
full placement history live in the area `DESIGN.md` §3.10 and decision log).
The copy is textually near-identical to `Database.Sql`'s today; divergence over
time is sanctioned — that is the point. The pump passes the protocol's existing Execute
message to the internal command-text entry, and the model's result sets ride the
generic result framing (`ResultComplete` carries the set's real
`AffectedCount`, so the model's one-row outcome sets report 1/0 on the wire).
Model-specific wire surface (binary command frames, if measurement ever demands
them) grows here, in this copy, without touching any other model. The machinery
design record (composition seam, state machine, error taxonomy, two-phase stop)
is documented in `Database.Sql`'s DESIGN.md server section, whose decisions
this copy currently mirrors. In particular, `StartAsync` awaits the configured
listener's `BindAsync` before starting the accept loop or returning; bind
failure terminally disposes the listener. `StopAsync` cancels accept, drains
sessions, then terminally disposes the listener. Stop is terminal, so restart
symmetry composes a fresh server and listener rather than reusing the disposed
pair. When this copy diverges, this section records the divergence. Since the
concrete-types plan's phase 4 (#1260) that lifecycle, the one this server carried
itself, is the base's state machine: the server supplies the bind-and-accept start
core and the drain-and-release stop core, and its sessions are internal sealed
leaves of `DatabaseServerSession`, which owns their identity, negotiated version and
authenticated principal. `Sessions` and `Engine` are read on the server itself;
the hosting layer reads `Engine` too, since phase 6 of the concrete-types plan deleted the
server's `Context`. A connection's teardown ignores
the engine session's disposal failure, now one `AggregateException` ("The session
failed to close."), as it ignored the `DatabaseException` before.

## Engine-owned background workers

The worker resilience tests observe Storage event 8 (PendingCommitsFlushed) for each create
and reopen and require no event 9 (GroupCommitWindowMissed). This proves the worker served
construction without a wall-clock latency assertion. Reopen receives an unproven writer so its
recovery scrub must commit; an unchanged clean open may require no grouped flush.
The listener filters process-wide events by names reserved to the Key-Value worker resilience
suite, including its failing, healthy and provisional databases, to prevent cross-test matches.

The group-commit flusher serves both published databases and provisional file sets (#1275).
It captures the storage snapshot before the instance snapshot, then flushes uncovered storages
without taking the engine lock: create/open holds that lock while its construction commits wait
on the durability gate. Published instances retain per-database failure/backoff handling.
Provisional offline storages are skipped, disposed storages from failed construction are tolerated,
and other provisional flush errors reach the constructing commit without a worker failure record.
Only SQL and Key-Value use this provisional pass; Documents, Blob and Graph retain self-help
while opening.

The same five-worker inventory as the SQL engine, spawned at creation on
engine-owned threads, quiesced on dispose (engines are data machines; R10):
group-commit WAL flusher (signal-driven), paced page write-back, checkpointer
(both file sets; data set through the coordinator so truncating checkpoint
records carry in-flight sequences; re-exports index registrations when they
changed — a backstop, since the B+Tree keeps its root page fixed through splits,
#1159),
**version purge — live** (the KV MVCC binding is real from the first cut, so the
purge duty is real: aborted-undo retries + reclamation below the minimum
snapshot floor), and the index-maintenance **stub** (the index layer has no
compaction yet — the stub matters more here than in SQL, since every delete
accrues a tombstone in the primary structure; the seam is kept stable for the
compaction feature). Cadence knobs live on `KeyValueDatabaseEngineOptions`.

The checkpointer and the purge worker follow the SQL engine's (#1254, #1226; Sql DESIGN.md,
"Engine-owned background workers"): a file set is checkpointed when its journal reaches
`CheckpointJournalSize` (its storage wakes the worker at once) or when `CheckpointInterval`
passed and its journal received records; the worker otherwise looks once a second. The purge
worker retries a deferred undo about 100 ms after the deferral and then at doubling delays up
to `MaintenanceInterval`, records a failure for its database with no backoff of its own (the
coordinator paces the retries, so one database's failing undo delays no other's), clears it once
a pass leaves that database no undo deferred, and keeps running. Both skip an offline database.

**A worker failure never ends a worker (#1268).** Every worker catches per database and per
file set: a failed checkpoint, page write-back or group flush of one database is reported
(`DatabaseEngineWorker.ReportFailure`, the worker's `Fault`), the pass goes on to the next
database, and later passes skip that database for `DatabaseEngineWorker.FailureBackoff` (one
second, PostgreSQL's error sleep, `src/backend/postmaster/checkpointer.c:286-346`,
`bgwriter.c:154-205`) while every other database keeps the worker's full pace (#1268 review); the
first pass that finishes that database's work clears its record; the engine reports `Faulted`
exactly while a worker holds a failure. A failure that took a database offline is not the
worker's: the engine lists the database in `OfflineDatabases`. The engine's pump runs a worker
again after the backoff if its loop ever ends early; since the engine derives from the root
`DatabaseEngine` (concrete-types plan, phase 4, #1260), whose pump this is, every worker it
runs is a `DatabaseEngineWorker`, whose loop lets nothing but cancellation escape, so a
registered worker whose pass throws is recorded and run again after the backoff instead. Before
#1268 one unexpected exception ended a worker for good. `KeyValueWorkerResilienceTests` covers a
checkpoint's and a write-back's page-write failures, a group flush's drain (#1252) or fsync
failure, a header slot write failure, a registered worker whose pass throws, a database whose
checkpoints keep failing (the other database keeps a pace a worker-wide backoff cannot reach: over
a shared six-second window more than twice the backoff's checkpoints, the floor that fails every
worker-wide backoff or stall of one backoff a pass, with the median second's share of the
no-fault checkpoints as a secondary signal; a smaller worker-wide slowdown can pass, and the
deterministic signal that would catch it is required follow-up work, the SQL engine's DESIGN.md,
"Engine-owned background workers"),
and a writer queued for a key lock when the database goes offline (by any of the three device
faults): it gets `COHDBK002` at once instead of waiting for the
reopen, because the coordinator ends every lock wait of an offline database
(`TransactionCoordinator.AbandonLockWaits`, wired to the data file set's offline hook), while
the writer that holds the lock keeps it, since an offline database undoes nothing.

**A failure that persists takes the database offline (owner decisions 25 of 2026-10-06 and
42 of 2026-10-07).** When
the checkpoint, page write-back, write-ahead flush or version-purge worker keeps failing on one
database for `WorkerFailureWindow` across at least `WorkerFailureMinimumPasses` failed passes in a
row (engine options, 100 s and three by default since owner decision 42 of 2026-10-07, which
replaced decision 35's count of a hundred passes: the window of Neo4j's ten failed checkpoints,
`community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:41-42`, at its
ten-second checkpoint check, `CheckPointThreshold.java:40`, measured on the engine's clock from
the first failed pass, so every worker gives up about that long after its first failure: about
100 s for a failing checkpoint, page write-back or write-ahead flush, about 101 s for a version purge's
full pass and about 102 s for a deferred undo, where the count took about 100 and 92 minutes;
the root `DESIGN.md`, "Why time, not a count";
a pass that fails both file sets counts once), the root worker base asks the engine to give up on
it, and `KeyValueDatabaseEngine.TakeDatabaseOfflineCore` takes the data file set offline with the
`StorageOfflineCause` that names the worker (`CheckpointFailures` and its siblings). A second checkpoint in a row
that fails while one of the database's journals holds `JournalSizeLimit` bytes (an engine option; zero, the
default, means four times `CheckpointJournalSize`, 1 GiB at its default) takes it offline with
`JournalSizeLimit`; one failure of a journal that reached the cap with no failure at all (#1283)
is retried like any other. Either way the database goes offline through the #1243 machinery:
the catalog file set follows through the data set's hook, every operation is refused with
`COHDBK002`, its lock waits end, nothing more is written to it, and the engine lists it in
`OfflineDatabases` until `OpenDatabaseAsync` reopens it (a hosted engine's application reopens it
with backoff, owner decision 22). The engine takes it offline on a thread-pool thread, never the worker's,
so a give-up that waits for a hung fsync of that database holds back none of the others, and it
finds the database in its published snapshot, without its registry lock, so it never waits for
another's open. Once the database is offline, or whenever it closes, the engine ends every
worker's failure record of it, so the engine reports `Running` at once and a reopened database
starts a new streak; a
database already offline or closed is not counted. `KeyValueWorkerResilienceTests` pins it: a
checkpoint failure that persists takes only its database offline once it lasted the window across the minimum of
passes (on a clock the test
moves; the minimum of passes inside the window leaves it online), a journal past the cap does on its second
failed checkpoint in a row, one transient failure of a journal already past the cap does not,
and a transient failure under the window does not (the window starts again once a checkpoint
finishes). The suite's other engines set the minimum of passes and the journal cap out of
reach, since they keep a database failing on purpose.

**A database closed outside the engine is skipped, then forgotten** (owner decision 33 of
2026-10-06, #1289). A database its holder disposed (directly; `session.Database` is the same
instance) stays registered only until its close ends. The close then tells the engine
(`KeyValueDatabaseEngine.ForgetClosedDatabaseCore`, through the root's shared
`DatabaseRegistry.Forget`), which stops tracking it, so a later `OpenDatabaseAsync` opens it again
from its file sets: a new instance with every committed entry. An in-memory database reopens with
its entries too: `InMemoryKeyValueStorageStrategy` keeps both file sets' streams until the
engine's disposal releases them (`DatabaseMemoryFiles`, `InMemoryKeyValueStorageStrategy.Release`),
and the open copies the closed streams' bytes and runs the same
recovery over them without the open's checkpoint (#1272); before #1272 an in-memory reopen got
empty storage. While the close runs, an open waits for it (the root
`DatabaseEngine.OpenDatabaseAsync`), `TryGetDatabase` and `GetDatabasesAsync` do not report the
database, a create of its name is refused as existing, and a drop or the engine's disposal waits
for the close, so nothing reuses the files under it. Before decision 33 `OpenDatabaseAsync`,
`TryGetDatabase` and `GetDatabasesAsync` returned the closed instance, which refused a new session
with `ObjectDisposedException`, until the database was dropped or the engine recreated. The
forget reads the engine's lock-free instance snapshot and takes the engine's lock only through a
bounded `Monitor.TryEnter` loop: a drop, an offline reopen and the engine's disposal dispose a
database while holding that lock, and that disposal waits for a close a holder started, so a
forget that blocked on the lock would deadlock with them. The engine's create, open and drop now
also check its disposal under the lock, so none of them registers or drops a database after the
engine's disposal has taken its snapshot. Because a drop and an offline reopen wait for a holder's
close under that lock, a close that stalls (a fsync that does not answer) stalls the engine's
other registry operations until it ends, and a drop's token is not observed meanwhile (root
`DESIGN.md`, "A stalled close stalls the engine's registry").

For the window between the close and the forget the workers skip the database:
`KeyValueDatabase.IsClosed` reads the base's disposed flag, `KeyValueDatabaseEngine.IsOpen` is
false for it, and the version-purge worker skips it in its pass and in its trigger wait, as do
the flush and write-back workers, which visit databases here (both file sets each). The
checkpointer skips it through the model's `IsCheckpointDue`, which is false for a closed
database (the pass is the engines' shared one, and its `IsOpen` check covers only a checkpoint
that raced the close). A close that was not idle leaves the data journal untruncated: when its
retry of a deferred undo still fails, the close keeps that writer in flight (#1226), so the closed
data set stays due for a checkpoint it refuses. Before the skips, the version-purge worker failed
on the closed database's disposed coordinator every pass (24 failed passes in half a second at
20 ms intervals); and when the checkpointer had a failure recorded for a database whose close was
not idle, every poll handed a lane the refused checkpoint, which kept the failure recorded.
Either way the engine reported `Faulted` for good and `Database.Hosting` reported it degraded
until the engine was recreated; the key-value server does not read the engine's state, so it
kept serving. The workers do what PostgreSQL's
background workers do with an object dropped under them: check that it still exists and skip it
quietly (autovacuum, `src/backend/postmaster/autovacuum.c:998-1000`, `:1859-1868`,
`:2510-2513`; the checkpointer's canceled fsync requests,
`src/backend/storage/sync/sync.c:400-411`, `:492-503`). A close here happens outside the
engine, which learns of it only when it ends, so until then the workers read the database's own
flag where PostgreSQL reads the cancellation.
`KeyValueWorkerResilienceTests.DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunningAndItsServerServing`
closes a database under 20 ms worker intervals and asserts that every pass succeeds, no worker
records a failure, the engine is `Running`, a server over the engine starts and serves a
handshake and a `PUT` to the other database, and the open returns a new instance holding the
closed database's 20 entries.
`OpenDatabaseAsync_DatabaseClosedOutsideTheEngine_ShouldReopenItWithItsEntries` closes it both
ways, in memory and on disk, with an entry of an uncommitted transaction: the reopened instance
is new, holds the committed entries and not the uncommitted one, takes writes, and a second
close and open keeps them.
`KeyValueWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
records a checkpoint failure for a database whose page writes fail, closes it with a rolled-back
transaction's undo deferred behind a bracket that holds every data page, and asserts that the
checkpointer's failure ends and the engine runs again (before the checkpointer's skip it stayed
`Faulted` for the test's 30 seconds).

## Storage operations (#1243, #1254, #1226)

**A failed fsync takes the database offline (#1243).** When a durable flush of either file
set fails, the storage goes offline (`Database.Storage` DESIGN.md, "A failed durable flush
takes the storage offline") and the other file set goes offline in the same moment: each
storage's `OnOffline` hook calls the other's `TakeOffline` before the failing call returns (until
the #1243 review it followed only when something next read the database's offline state, and
the workers could write the other set in between), so no file of the database changes after the
failure — PostgreSQL's `PANIC` on a failed WAL fsync
(`issue_xlog_fsync`, `src/backend/access/transam/xlog.c:9877-9937`; the commit critical
section in `RecordTransactionCommit`, `src/backend/access/transam/xact.c:1470-1583`; and
`data_sync_retry` off, `src/backend/storage/file/fd.c:3966-3987`), without stopping the process.
The command whose commit flush failed gets `DatabaseTransactionCommitUnconfirmedException`, its
message leading with `COHDBK002` (owner decision 24); every later operation — a new session, a
typed or text command, BEGIN, and the COMMIT or ROLLBACK of a transaction open at the failure —
is refused with `DatabaseOfflineException`, code
`COHDBK002`, carrying the storage's `StorageOfflineException`. `KeyValueDatabaseServer` answers a
command on an existing session, and a handshake for the database, with `Unavailable` and the
coded message. The workers skip the database, and closing its sessions writes nothing. A
storage bracket whose commit record was written before its flush failed is reported as
unconfirmed, never refused (`StorageOfflineException.CommitRecordWritten`). The engine stays
`Running`; `KeyValueDatabaseEngine.OfflineDatabases` names the database, and `Database.Hosting`
reports the application unhealthy while it is listed. A header slot write that fails takes the
database offline the same way (#1268): no header write may run again in that process, so no
checkpoint could truncate the journal, and a database that kept accepting commits would grow it
without bound; the refusal's message names "a write of the file header".
`KeyValueDatabaseEngine.OpenDatabaseAsync(name)` disposes the offline instance without writing
and reopens the file sets; recovery keeps the unconfirmed commit if its record's bytes reached
the media, and aborts every transaction that was open. `KeyValueStorageOperationsTests` covers
it with a fault-injecting strategy over durable in-memory handles, running every worker's pass
before any session operation and reopening with and without the unconfirmed record's bytes; the
test strategy can now reopen a database it created, which the old one could not. Two more tests
fail one file set's journal fsync — the data set's on a commit, the catalog set's at its
checkpoint — and check, with no engine call in between, that the other set is already offline
and that a pass of every worker leaves its files unchanged.

**Buffer pool and checkpoint options (#1254).** `BufferPoolCapacity` (32 MiB per data file set;
whole 8 KiB pages, at least 1 MiB), `CheckpointJournalSize` (256 MiB; zero for time only; not
negative) and `CheckpointInterval` (5 minutes, was 30 seconds); `Create` validates all three and
`KeyValueDatabaseEngineBuilder` carries them. The catalog file set keeps a 128-page (1 MiB) pool
(`KeyValueDatabaseEngine.CatalogBufferPoolPages`), so an open database costs up to about 34 MiB of
pool memory once it has touched that many pages, plus, in memory, its data and its journal (up to
the checkpoint size, briefly twice that while the buffer doubles past it). The reasoning is in
`Database.Storage` DESIGN.md ("Capacity", "Checkpoint triggers"). The checkpoint worker never
waits for a statement: one holding a database's apply gate runs that database's checkpoint as it
ends (`TransactionCoordinator.TryCheckpoint`), so a long command in one database cannot stop the
other databases' checkpoints.

**Deferred undo is retried on its own backoff (#1226).** The version-purge worker retries a
failed undo about 100 ms after the deferral, then at doubling delays up to `MaintenanceInterval`.
A retry that fails makes the engine report `Faulted`; the first pass with no failure and no undo
still deferred clears it. A full pass that fails keeps the database's failure until a later full
pass completes: the retries between full passes do not redo its work (owner decision 42 review;
Sql DESIGN.md, "Deferred undo is retried on its own backoff"). That later full pass is the
database's own retry, a `FailureBackoff` after the failure, not the next `MaintenanceInterval`
(owner decision 46), so a full pass that keeps failing gives up at about 101 s, like the other
workers.

## Two file sets per database

The data set (entries + primary index pages — index pages ride the data storage's
transactional page surface, no separate index file) and the catalog set
(registrations + format marker), both via the internal `KeyValueStorageStrategy`
base — file-backed under `RootPath`, in-memory otherwise, the SQL strategy pattern.
The strategy is `internal abstract` since the concrete-types plan's phase 4 (D9):
no shipped code outside this assembly supplied one, so the public interface and the
public option property went, and this assembly's durability and fault-injecting
test strategies derive through its test-only grant.

### Names and files (#1392)

Every strategy maps a database's name through the area's one shared function,
`DatabaseFileNames` (root design, "A database's name maps to its files through one
shared function"). On disk a database is one directory under `RootPath`, named for
the invariant lower case of its name:

```
<RootPath>/
└── sales/             database created as "Sales", "sales" or "SALES"
    ├── keyvalue.dat   data file set
    ├── keyvalue.log
    ├── keyvalue.bak
    ├── catalog.dat    catalog file set
    ├── catalog.log
    └── catalog.bak
```

- **Every spelling is one database.** Before #1392 `FileSystemKeyValueStorageStrategy`
  built `<RootPath>/<name>/<name>.dat` from the spelling it was given, so on Linux the
  declaration of `SALES` did not find a database an earlier build created as `sales`
  and created a second, empty one: Ubuntu CI failed
  `KeyValueEngineDeclarationTests.BuildAsync_DeclaredDatabaseExists_ShouldOpenItWithItsData`
  exactly so. Create, open, drop, declarations and existence checks now all reach the
  same directory, and a create of `SALES` after `sales` is refused as existing whether
  `sales` is open or not.
- **The catalog is a file set, not a name.** A strategy member takes the database
  name and a `catalog` flag. The catalog used to be the storage named
  `<name>.catalog`, so a database named `sales.catalog` was the catalog of `sales`;
  now it is `catalog.*` inside the database's own directory, and a drop deletes the
  directory, both sets at once. An open whose data set exists without a catalog set
  still creates the catalog set, as before. A directory without the data set, which an
  interrupted create or drop leaves, is no database: an open does not find it, a
  create refuses it with a message that names the remedy (a stale catalog set must
  never meet a new data set), and a drop deletes it.
- **The reported name.** A created database reports the name its create used; an
  opened one reports the spelling its data set's storage header kept from the create
  (`DatabaseFileNames.GetOpenedName`), whatever spelling opened or declared it. The
  catalog set's header is labeled `<name>/catalog`.
- **In memory** the engine's `DatabaseMemoryFiles` keys each set by the database's
  storage name and the set, with the same rules.

`KeyValueDatabaseNameTests` pins each case in memory and on disk, asserting the exact
directory and file names it leaves with ordinal comparison, so the tests prove the
mapping on Windows, whose file system folds case, as on Linux.
The primary index bootstraps at database creation inside a durably-committed
bracket (the self-committing DDL posture), then persists the format marker and
then its registration as catalog self-commits — in that order, so a registered
primary index always carries the marker of the engine that built it; a crash
between tree build and registration leaves only an orphaned root page (safe
leak), repaired by re-bootstrapping on the next open.

## Error model

Storage I/O failures (StorageIOException or IOException) are DatabaseException with an Error
diagnostic COHDBK005 (#1364); corruption keeps DatabaseCorruptionException / COHDBS003.
Aborted-transaction refusals keep their own structured diagnostic and the original coded cause.
The documented SQLSTATE mappings are 58030 for I/O and XX001 for corruption; no new carrier
member is introduced. These failures abort an explicit transaction, retain the coded cause,
and require rollback before further statements. In autocommit the next statement can run.
The wire returns ExecutionFailure and preserves the session. Offline/unconfirmed commit
classification retains its existing precedence and outcome. Open failures in either the data
or catalog file set carry the I/O code, release opened resources and permit a later retry.

This follows PostgreSQL's ERROR corruption report in src/backend/storage/buffer/bufmgr.c:8866-8868,
and its report/transaction-abort/session-loop path in src/backend/tcop/postgres.c:4612,4629.
The implementation uses static type checks and the existing transaction abort mechanism.


Storage corruption is `COHDBS003` (#1160). The engine translates a
`StorageCorruptionException` into `DatabaseCorruptionException`, retaining the storage
cause and page id, naming the database and page, and advising restoration from a verified
backup. Its structured diagnostic crosses the existing wire execution-failure path; a
failed read never becomes an absent row, document, node, key or blob. Legitimate slot
reclamation still reads as absence. Create/open failures receive the same translation
from the root lifecycle after the model has released its resources. Storage format 4
reserves zero headers for entirely zero pages and identifies reclaimed pages separately.

Corruption translation searches coded inner exceptions and every aggregate branch with
`StorageCorruptionException.Find`; an existing `DatabaseCorruptionException` is retained.
This applies to read/statement and transaction failures as well as synchronous and deferred
create/open failures. A refused open retains any existing model diagnostics in addition to the
outer `COHDBS003`; Graph, Documents and Blob also carry their unrecoverable-files diagnostic
(`COHDBG018`, `COHDBD003` and `COHDBB004` respectively). The original storage exception remains
the inner cause, preserving its page context. Graph abort messages prefix a cause's code only
when the message does not already contain that prefix.

`DatabaseException` (area root) for misuse and model errors;
`DatabaseNotFoundException` for an open request whose storage does not exist;
`DatabaseParseException` for grammar violations (→ `ParseFailure` on the wire);
`DatabaseTransactionAbortedException`/`DatabaseTransactionDeadlockException`
(retryable) for MVCC conflicts — kernel exceptions are translated at the model
boundary, never leaked raw; `DatabaseTransactionCommitUnconfirmedException` (not
retryable) for a commit whose record was written but whose fsync failed, and
`DatabaseOfflineException` (`COHDBK002`, → `Unavailable` on the wire) for every operation
after it ("Storage operations").

An entry page the command cannot read fails the command. Storage reports
StorageCorruptionException (with the page id) for checksum or slot geometry damage,
StorageIOException for a cut-short page or a pool whose frames are all pinned, and
IOException for device failures. The engine translates corruption to DatabaseCorruptionException
(COHDBS003), and I/O to DatabaseException with COHDBK005. Every command resolves its key by a
primary-index seek, and the seek fetches the
entry record with `Storage.TryReadRecord` and the key space's owner id (#1342): an index
entry whose record was reclaimed beneath it (its slot deleted or reverted, its page freed
or reallocated) reads as absence, and anything else that stops the read fails the command.
That includes a record that passes its page checksum and does not decode: the read checked the
page's type and owner, so the record is a key-space entry record, and one that does not decode
is damaged, not reclaimed. It fails the command with `StorageCorruptionException` (carrying the
page id, with the slot and the defect in its message), as the Graph, Documents and Blob codecs
raise on a malformed record; before, the key read as missing. Get, Exists, Scan and the
resolution a Put or Delete starts with all fetch through the same read, so each fails this way
(#1362). `KeyValueRecordCodec.Decode` throws `DatabaseTypeException` for a record too short for its
stamps, a malformed or truncated key or value component, and bytes past the value; it has no
`false` result a caller could read as an absent key. The read holds a pin, not a latch, so it can
copy a slot while a writer reclaims it (the purge freeing and clearing it; a failed command's
bracket rollback restoring the page did too until #1371, when the storage read began confirming
itself against the restore), and that copy is torn rather than damaged: a failed decode is
confirmed by reading the slot again, with the same owner check, before it is reported. A slot
reclaimed by then reads as absence, and a re-read that decodes is the entry. A re-read that still
does not decode is corrupt when its bytes equal the read before it, because damage is stable;
when they differ the slot is read once more, up to two re-reads. The confirmation narrows the
torn-copy window and does not close it: a writer descheduled half-way through rewriting the page
leaves the same torn bytes for every read (the SQL DESIGN's "Error model" measures the race and
names the follow-up that would close it).
The latest-version check under the key lock makes the same split: a reclaimed version is the
retryable write-write conflict, and a page it cannot read, or a live slot whose record is too
short for its stamps (#1362), is the storage error. Unlike SQL's, the key-value short-record
branch is defensive only: a Put or Delete resolves its key under the key lock, through the read
above, immediately before the check, and that read already fails a record too short for its
stamps, so only damage landing between the two reads reaches it, and no test drives it. Until #1342
both caught `StorageException`: a corrupt entry page made its keys read as missing, and a
PUT over such a key resolved it as missing and then failed with a retryable conflict that no
retry could clear.

## Shared MVCC composition (#918 — area DESIGN §3.10)

`TransactionCoordinator` and `RecordSpaceVersionStore` now live in the existing
`Database.Transactions` child root. `KeyValueTransactionRecordSpace` supplies
entry reads, transactional updates/deletes, and the existing packed location
codec. `KeyValueRecordCodec` retains key/value payload encoding and delegates
stamp operations to `RecordVersionStamp`; the shared
[16-byte layout](../../Assimalign.Cohesion.Database.Transactions/docs/DESIGN.md#record-stamp-prefix-the-16-byte-contract)
is the contract for subsequent models. The instance's thin
`TransactionSource` resolver (the index manager's delegate since #1258) retains the engine's `DatabaseException`
for a missing statement bracket; Indexing's `BTreeRecordVersionIndex` binds the
primary index to the shared undo ledger without a reverse dependency.

Recovery ordering is unchanged: re-attach the primary index, analyze and scrub
records, scrub the index with the same classification, then complete the
deferred checkpoint before ensuring the primary index exists. The coordinator
retains the same journal append/checkpoint gate, statement apply gate, and
snapshot-based safe prune bound as the extracted copies.

## Non-goals (current cut)

- **TTL/expiration** — the area model table lists TTL for this model; it is
  deferred (#919), and the surface was deliberately shipped without
  `ExpiresAt` so etag semantics landed clean first.
- Named key spaces (multiple ordered key spaces per database) — the catalog
  reserves the concept; the engine currently owns one implicit key space.
- Multi-key atomic batches, `Serializable` isolation, secondary value indexes,
  index compaction (the stub worker's future body).

## Database scope conformance (A5)

Each session captures one database instance and its operation executor.
`KeyValueDatabase` validates that instance identity before all five typed
operations, including deferred scan enumeration; they take a `KeyValueDatabaseSession`,
so a session of another model cannot be passed at all. Typed requests carry no database
selector; key bytes are data even when they resemble qualified names. The text
grammar has no database selector or server administration verb. Database lifecycle
operations belong to the host-owned engine.

`KeyValueDatabaseScopeTests` mirrors the SQL/Blob guard: two databases contain
the same key with different values, attempts to pass a foreign session fail,
commands cannot select another database, and attempted server/database commands
leave the binding and engine inventory unchanged. The tests use public execution
behavior without reflection.

## AOT posture

No reflection, no runtime codegen: byte spans, the shared tuple codec, and
boxed scalars only at the result-row boundary (the Execution family's shape).

## Model-owned wire family (#1015)

KeyValuePair.Protocol owns the KeyValuePair request and tabular result codecs; the shared protocol
contains only mechanism. [Wire format](WIRE-PROTOCOL.md) specifies every message and
scalar component for independent clients. The server binds KeyValueProtocol.Family
once on accept and retains its existing model payloads. The shared startup version advances
to 2.1 for #1105 negotiation, retaining #1126's major-2 Documents projection contract, and rejects incompatible majors before
authentication. Result materialization belongs to
Database.KeyValuePair.Client. Transport listeners remain supplied through generic IDuplexConnectionListener.

Payload offsets are zero-based and exclude the shared five-byte frame header. `Execute` (5)
starts with a signed 32-bit big-endian statement byte length `S` at bytes 0–3, followed by
`S` UTF-8 statement bytes at byte 4 and a nonnegative signed 32-bit big-endian parameter
count at bytes `4 + S`–`7 + S`. Each parameter beginning at byte `Q` has a nonnegative
signed 32-bit big-endian name length `N` at bytes `Q`–`Q + 3`, `N` UTF-8 name bytes at
byte `Q + 4`, a nonnegative signed 32-bit big-endian encoded-value length `V` at bytes
`Q + 4 + N`–`Q + 7 + N`, and `V` self-describing scalar-component bytes at byte
`Q + 8 + N`. `ResultHeader` (6) starts with a nonnegative signed 32-bit big-endian column
count at bytes 0–3; each repeated column has the same four-byte name length and UTF-8 name,
followed immediately by one unsigned `DatabaseType` byte. `ResultRow` (7) concatenates one
self-describing scalar component per result field from byte 0 through the payload end, with
no count prefix. `Transaction` (9) is reserved and has no implemented payload; supported
key-value transaction commands travel as statement text in `Execute`.

`ResultComplete` (8) is the implemented family's one fixed-width payload. Its encoder emits
exactly eight bytes: bytes 0–7 (bits 0–63) are the signed 64-bit affected count in big-endian
order. Key-value query sets use `-1`; outcome sets preserve their affected count of `1` or
`0`. The packet view below shows that complete fixed-width payload.

```mermaid
packet-beta
0-63: "Affected count (i64, big-endian)"
```

## Phase 29 hosting composition

`AddKeyValue(string name, Action<KeyValueDatabaseEngineBuilder>)`
replaces eager `AddKeyValueDatabase` and the sibling application `AddKeyValueServer`.
The verb registers a dependency-free factory and returns the application builder. The engine name
is its first argument (owner decision 52 of 2026-10-09): the verb reserves it through the root
seam's named `AddEngine(name, factory)`, so a duplicate fails at the call, and the builder created
for it reports it as `Name`. No options type carries the name (B3 of the engine extensibility
design).
Application Build executes its callback. The builder has the SQL builder's engine-level shape
(B3):

- `Options` is the engine's `KeyValueDatabaseEngineOptions`, values only, `FileSystemPath?
  RootPath` included. `Build` copies it before anything is created and checks the copy, so a
  change made after the build began never reaches the engine; the fourteen properties that
  mirrored the options on the builder are gone.
- `AddDatabase(name)` declares a database the engine owns. The build opens it, or creates it when
  `OpenDatabaseAsync` throws the root's `DatabaseNotFoundException`, in declaration order, after
  the workers and servers are attached; a failure disposes the engine. A second declaration of the
  same name (ignoring case) is refused at the call, and the built engine refuses to drop a declared
  database with `DatabaseObjectLockedException` (owner decision 56 of 2026-10-09), through the
  wording the SQL engine uses (`DatabaseDeclarations`, compiled from the root's shared source).
  A name that breaks the area's one name rule (#1392: 1 to 128 ASCII letters, digits, `_`, `-`
  and `.`; no leading, trailing or doubled dot; no Windows device name before the first dot) is
  refused at the call too, before the engine exists, with the same verdict on every OS: `..`,
  `a/b` and `a:b` alike.
- The engine applies that name rule in its create, open and drop cores, after the root base's
  checks (`DatabaseFileNames`, shared with every model; the B3 review). A database's files live in
  a directory named for it under `RootPath`, so before the rule `CreateDatabaseAsync("../x")` wrote
  a file set beside the root.
- `PageWriteBackInterval` and `PageWriteBackBatchSize` must be positive, as on every other model
  since the B3 review: a zero interval spun the write-back worker's wait, and a zero batch failed
  every pass in the storage until the failure policy took the databases offline.
- `AddServer(Action<KeyValueDatabaseServerOptions>)` creates a `KeyValueDatabaseServer` over the
  engine from options the callback configures, and disposes the listener the options carry when
  the server cannot be created; `AddServer(Func<KeyValueDatabaseEngine, DatabaseServer> factory)`
  and `AddWorker(factory)` take any product.
- `Build()` bridges `BuildAsync(CancellationToken)` on the thread pool; the token is observed
  before the engine is created and before each declared database.

It constructs the engine before invoking nested `AddWorker` and `AddServer`
factories. No DI or configuration enters this model package. Direct
`KeyValueDatabaseEngine.Create(name, options)` stays available, keeps a copy of its options, and
its composition is complete when it returns: an engine created without its builder takes no
worker or server and declares no database. Every option refusal names the engine and the option
(`Key-value engine '{name}': CheckpointJournalSize must not be negative.`), so a host that builds
several engines says which one was misconfigured.

The engine's worker pumps are the root `DatabaseEngine`'s: every worker, built-in or
factory-supplied, is a `DatabaseEngineWorker`, which the engine pumps and quiesces
during disposal. `KeyValueDatabaseEngine.Servers` is read-only observation;
application Build flattens it for lifecycle, while the engine owns server disposal,
followed by worker quiescence and database closure. Cleanup attempts independent
children after a failure. Factory engines are application-owned; instance
registrations remain caller-owned. Database create/open/drop/lookup accept
`DatabaseName`.

**The sealed builder (concrete-types plan, D5, phase 4, #1260).**
`KeyValueDatabaseEngine.CreateBuilder(name)` returns the `public sealed`
`KeyValueDatabaseEngineBuilder`, which has an internal constructor, so the verb and
`CreateBuilder` are the only ways to get one. Its factories are typed over the
engine, `AddWorker(Func<KeyValueDatabaseEngine, DatabaseEngineWorker>)` and
`AddServer(Func<KeyValueDatabaseEngine, DatabaseServer>)`, so a server factory needs
no cast (`KeyValueDatabaseServer.Create(engine, options)`). This reverses the
phase-29 ruling that kept the factories on the root interfaces and had server
callbacks cast to the model engine once, to avoid ambiguous overloads: a sealed
builder has one factory overload of each, typed (B3 added `AddServer(Action<KeyValueDatabaseServerOptions>)`, which binds by delegate type, as the SQL builder's does). `Build()` returns the engine. The builder runs the shared `DatabaseEngineBuilderState`, which
hands the products to the engine's internal `Compose` method one factory at a time;
the engine attaches each through the root base, which refuses a product attached
twice, a server that fronts another engine, and a worker whose name another worker
of the engine has (ordinal, ignoring case), and then freezes the composition. The
state disposes what a failed build leaves unowned and then the engine.
`KeyValueEngineCompositionTests` pins every path. The storage strategy option is
internal on the builder as on the options (D9).

## Concrete types (concrete-types plan, phase 4, #1260)

The model is the first to adopt the root bases
([plan](../../../../docs/programs/DATABASE_CONCRETE_TYPES_PLAN.md) §7). Its public types
are sealed leaves; it has no public interface left, and no `Abstractions/` folder.

| Type | Base | Was |
|---|---|---|
| `KeyValueDatabaseEngine` | `DatabaseEngine` | a sealed `IDatabaseEngine` |
| `KeyValueDatabase` | `DatabaseInstance` | `IKeyValueDatabase` and the internal `KeyValueDatabaseInstance` |
| `KeyValueDatabaseSession` | `DatabaseExecutionSession<KeyValueRequest>` | an internal `IDatabaseSession` |
| `KeyValueDatabaseTransaction` | `DatabaseTransaction` | an internal `IDatabaseTransaction` |
| `KeyValueDatabaseServer` | `DatabaseServer` | a sealed `IDatabaseServer` |
| `KeyValueDatabaseServerSession` (internal) | `DatabaseServerSession` | an internal `IDatabaseServerSession` |
| `KeyValueDatabaseEngineBuilder` | none | `IKeyValueDatabaseEngineBuilder` and its internal implementation |
| `KeyValueStorageStrategy` (internal abstract) | none | `IKeyValueStorageStrategy` |

- **Typed surface without casts.** The engine re-exposes `CreateDatabaseAsync`,
  `OpenDatabaseAsync` and `GetDatabasesAsync` typed (`KeyValueDatabase`) with `new`
  members over the base's public members; a database re-exposes its `Engine` and
  `CreateSessionAsync` (`KeyValueDatabaseSession`); a session its `Database`,
  `CurrentTransaction` and both `BeginTransactionAsync` overloads
  (`KeyValueDatabaseTransaction`); the server its `Engine`. Each `new` member awaits
  or reads the base's public member and casts once, so the base's checks always run.
  `TryGetDatabase(DatabaseName, out KeyValueDatabase)` is a typed overload of the base's
  lookup, not a `new` member (the parameter types differ): it calls the base's public
  member and casts once, an `out var` or `out _` call on the engine binds it, and an
  explicitly typed `out DatabaseInstance` binds the base's. The typed operations
  (`GetAsync` and its siblings) take a `KeyValueDatabaseSession`.
- **What the bases own now.** The engine base owns the name, the model, the workers'
  pumps (the model no longer compiles `shared/DatabaseEngineWorkerPump.cs`), the state
  fold, composition and the disposal order (servers, the pumps and the workers, then
  the databases); the database base owns the disposed flag; the session base owns the
  session state, the session's transaction and the "already active" check; the
  transaction base owns the whole end state machine; the server base owns the
  lifecycle. The model supplies its vocabulary: `COHDBK001` and `COHDBK002`, the
  kernel calls and the translation of the kernel's exceptions. It keeps its
  per-command rule for ordinary errors (the owner's 2026-10-04 decision); storage corruption
  and I/O failures abort through the base's `AbortAsync` (#1364). Commands do
  not take the session's operation hold.
- **What changed for a caller** (plan §6.4): BEGIN on an active session fails with
  "A transaction or operation is already active on this session." (was "A transaction
  is already active on this session."), and fails that way before the Serializable and
  offline refusals; BEGIN refuses a transaction the kernel ended under its caller
  (`COHDBK001`) before it checks the token, where a canceled token was reported first; a
  canceled token is refused by `CreateSessionAsync` and both execute seams before the
  offline (`COHDBK002`) and refusing-transaction (`COHDBK001`) refusals, which were
  reported first; a transaction whose session closed while its database was offline
  reports `Faulted` (was `Active`), and refuses everything either way; a closed session
  fails with "The session is closed." (was "Session is not open. Current state:
  Closed."); a rollback after a commit fails with "The
  transaction is Committed; a committed transaction cannot roll back." and a commit of
  an ended transaction with "The transaction is {state}." (both were "Cannot … in
  state …"); a commit while a command runs fails with "An operation of the transaction
  is still running; commit after it completes."; a command refused while the caller's
  commit or rollback runs or after it ended says "operation" where it said "command";
  the teardown cause a later commit names is "The session closed before the
  transaction ended."; a session that fails to close reports one `AggregateException`
  ("The session failed to close."), where the transaction's failure escaped
  unwrapped; and the engine's disposal aggregate is "One or more components of engine
  '{name}' failed to close." (was "Engine disposal encountered failures."). When two or
  more databases fail to close, the engine aggregate carries them inside one nested
  `AggregateException` ("One or more key-value databases failed to close."), where each
  was an inner exception of the engine's; one failure stays flat. The
  engine's guards check the name, then disposal, then the token (disposal used to
  come first, and `TryGetDatabase` did not check the name), `GetDatabasesAsync` checks
  disposal when it is called, and a blank engine name is refused by `Create` and
  `CreateBuilder` (`ArgumentException`, parameter `name`; it was the options'
  `EngineName` until B3). A worker's blank name is
  refused by its own constructor inside its factory ("A worker must have a diagnostic
  name." is gone), and the engine disposes every worker last attached first, factory
  workers before the built-in ones (it used to dispose the checkpointer first).

## Diagnostics

The create core accepts the root's explicit `object? creationState` parameter (#1349 review).
This model's public create uses null/defaults; the root continues to own validation and
create telemetry, with no per-call options allocation or additional model event.

The model raises its own events through one internal event source, named for the assembly:
`Assimalign.Cohesion.Database.KeyValuePair`
(`src/Internal/EventSource/KeyValueDatabaseEventSource.cs`; database event-sources plan, batch
B5). It reports the wire server and its sessions. A command's outcome, the engine, its
databases and its workers are the root source's (`Assimalign.Cohesion.Database`); kernel
transactions, locks and storage are the Transactions and Storage sources'. The SQL, Graph and
Blob servers write events 1-9 with the same ids, names and payloads from their own sources
(plan, D2), so one provider list and one log query cover the four servers.
The conventions every Database source shares (the failure rule, the ending rule, peer hang-ups,
bounded peer-sent names) are in the area's
[`DESIGN.md`](../../../../docs/resources/Database/DESIGN.md#diagnostics-one-event-source-per-assembly).

| Id | Event | Level | Keyword | Payload |
| --- | --- | --- | --- | --- |
| 1 | `SessionAccepted` | Verbose | `Sessions` | `engineName`, `sessionId`, `activeSessions` (this one included, as the accept loop counted them) |
| 2 | `SessionRejected` | Warning | — | `engineName`, `reason` (`SessionLimit`), `activeSessions`, `maxSessions` |
| 3 | `HandshakeRefused` | Warning | — | `sessionId`, `database` and `principal` (as the startup named them, cut to 256 characters; empty before it was read), `code` (the `ProtocolErrorCode` name), `detail` (the error frame's message, cut to 1024 characters) |
| 4 | `HandshakeTimedOut` | Warning | — | `sessionId`, `timeoutMilliseconds` (the timeout lapsed anywhere in the handshake: at a read, or while the database opened, a frame was written, the authenticator ran or the session was created) |
| 5 | `SessionClosed` | Verbose | `Sessions` | `sessionId`, `reason`, `durationMilliseconds` (zero for a session accepted while the event was off) |
| 6 | `SessionProtocolViolation` | Warning | — | `sessionId`, `exceptionMessage` (cut to 1024 characters) |
| 7 | `SessionFaulted` | Error | — | `sessionId`, `exceptionType` (full name), `exceptionMessage` |
| 8 | `SessionCleanupFailed` | Warning | — | `sessionId`, `exceptionType`, `exceptionMessage` |
| 9 | `SessionsAborted` | Warning | — | `engineName`, `sessions`, `drainTimeoutMilliseconds` |
| 10 | `StatementParseFailed` | Error | — | `sessionId`, `database`, `code`, `exceptionType`, `suppressedCount` (int) |
| 11 | `TransactionCleanupFailed` | Warning | — | `transactionId`, `exceptionType`, `exceptionMessage` (bounded to 1024 characters) |

Event 10 reports a wire command that failed to parse before the typed execution member ran.
It writes only the exception type and code, never command text or the parse message. It is
throttled across all sessions to at most one event per second, with the count suppressed since
the preceding report (saturated at `int.MaxValue`, as Tcp's `AcceptBackoff` payload is).
The `total-statement-parse-failures` counter is exact: every occurrence increments it, including
suppressed reports and failures while nobody listens. A parse failure returns `ParseFailure`
and leaves the connection ready. Offline and transaction refusals happen before parsing and
produce no parse-failure event or root statement event.

`SessionClosed`'s `reason` is `PeerClosed`, `Terminated`, `IdleTimeout`, `Shutdown` (the
graceful drain closed it at a frame boundary), `HandshakeTimedOut`, `HandshakeRefused`,
`ProtocolViolation`, `Cancelled` (the server stopped or aborted the session mid-frame),
`ConnectionAborted`, `TransportFailed`, `Faulted`, or `Unknown` (an out-of-memory failure, which
the pump does not catch). The handshake refuses with `ProtocolViolation` a first frame that is
not Startup and an answer that is not AuthenticateResponse, with `UnsupportedVersion`,
`DatabaseNotFound` and `AuthenticationFailed`, and with `Unavailable` an offline database
(#1243). `SessionFaulted` is the catch-all that used to leave only an internal-error frame; the
handshake timeout and the session-close failure were silent before.

**A peer that hangs up is not a fault** (plan D9). A transport failure from the peer's side
reaches the pump as the TCP driver's raw `SocketException`, a `ConnectionException`
(`ConnectionResetException`, `ConnectionAbortedException`), or an `IOException` that wraps one.
The four servers classify it with one rule, in identical private copies (`TransportCloseReason`,
plan D2), and close the session with `TransportFailed` or `ConnectionAborted`, writing no
`SessionFaulted` and no error frame. A bare `IOException` stays a fault: it can be the storage
device's. The TCP driver reports a reset its receive sees as the end of the stream, so a session
idle in its ready loop that the peer resets closes as `PeerClosed`; a reset that fails a send the
pump is blocked in raises the raw `SocketException`, which was a `SessionFaulted` (Error) before
this rule. Under the duplex contracts (#1459) a transport signals `ConnectionClosed` as soon as it sees
the close, and the session's lifetime follows that token, so a reset can also end the blocked read
or write as a cancellation the server did not request: the pump reports it as `TransportFailed`
too, and keeps `Cancelled` for the server's own stop or abort. **A session leaves the server once**: its cleanup releases its resources in a `try`
and completes the session with the server in the `finally`, so a disposal that throws, which
still propagates, neither leaves `current-server-sessions` raised nor skips `SessionClosed`, and
its `MaxSessions` slot is freed.

Counters are maintained whether or not anyone listens, on accept, rejection, close and command
parse failure, never per row: `current-server-sessions` (gauge: up when the accept loop
registers a session, down when the session's completion removes it), `total-server-sessions`,
`total-rejected-sessions`, and `total-statement-parse-failures`.

Every write sits behind `IsEnabled(level, keywords)`, and a session reads its start timestamp
only while `SessionClosed` is on. No payload carries command text, keys, values or
authentication evidence; principal names are kept as identifiers. A string a peer sent can reach
a payload before authentication, and a frame may hold 16 MB, so the handshake's `database` and
`principal` are cut to 256 characters and a `detail` or violation message to 1024, marked with
`...` (event-source.md rule 11). `KeyValueDatabaseEventSourceTests` checks the name, the strict
manifest, the gauge's return, the counters, that no write allocates while nobody listens, and
events 1-7 and 9 once each with their payloads over real sessions on the in-memory driver: every
handshake refusal code, a timeout at a read and inside the authenticator, and the bound on an
oversized startup; a peer that resets its TCP connection while its session idles and while the
server writes (a transport reason, never `SessionFaulted`; the write case, reset once the peer's
unread pongs stop growing, accepts only `TransportFailed` or `ConnectionAborted`, so it fails if the
reset stops reaching a send); and a connection whose disposal throws
(`SessionClosed` once, the gauge restored). `SessionCleanupFailed` is covered only by the
allocation check: no test double makes the session's own close fail yet.

## Connection contracts

The server accepts `IDuplexConnection` values through `IDuplexConnectionListener`:
framed database traffic requires the byte-pipe contract. The transport listener's live
`Connections` collection is observational; the server owns every connection it accepts,
performs its I/O, and drains, aborts or disposes it during shutdown. Disposing the listener
alone does not close accepted connections. `ConnectionClosed` signals local close, abort,
peer close and, at the latest, completion of connection disposal.

### Breaking changes from 10.0.0-preview.1 (#1459)

`KeyValueDatabaseServerOptions.Listener` changes from `IConnectionListener?` to
`IDuplexConnectionListener?`. Changing the property's getter and setter signatures breaks
preview.1 binaries and source that supplies a listener typed only as `IConnectionListener`.
This preview break is accepted under owner decisions 15 and 58. Rebuild consumers and use
`IDuplexConnectionListener` for listener variables and custom implementations. Custom listeners
can derive from `DuplexConnectionListener`, supply `Id`, and implement `AcceptCoreAsync`
returning `ValueTask<IDuplexConnection>`; the base handles shared accept dispatch and live
connection tracking. Custom byte-pipe connections derive from `DuplexConnection` or implement
`IDuplexConnection`. Built-in duplex listeners already satisfy the new contract. Database wire
framing, handshake and model messages are unchanged.

ProtocolPayload string decoding uses strict UTF-8. The shared startup/error reader refuses invalid
sequences with ProtocolException; the server reports fatal ProtocolViolation and closes the session.
Model statement, parameter-name and result-header strings use that same shared strict reader.

## Shared startup negotiation (#1105)

The endpoint participates in protocol 2.1: Startup may carry capability names and Ready returns the agreed version with an empty capability list. Negotiated 2.0 still sends empty Ready. Different majors are refused. This model does not register the core Diagnostics capability and its channel rejects that frame in both directions. ParseFailure and ExecutionFailure keep the existing Error layout, with CODE: message text. Parser codes COHKV001–018 and fallback COHKV019 are published in COMMANDS.md. Structured Diagnostics adoption can register the core capability in a follow-up.

## Interrupted drop and missing sidecars (#1158)

Drop explicitly deletes keyvalue.dat, then the remaining *.dat files, before recursively
removing the directory. Open requires each existing file set's original journal and backup;
missing sidecars receive structured COHDBK004 and no replacement files are created. Preserve
all files and restore the complete backup, since a lost journal may hold acknowledged commits.


## Catalog component

The Catalog component is part of `Assimalign.Cohesion.Database.KeyValuePair`, with public types in `src/Catalog/` and helpers in `src/Internal/Catalog/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Catalog: KeyValuePair catalog design

#### Catalog: Design intent

The smallest catalog in the model family, on purpose. A key-value database's only
durable metadata is what re-attaches it on open: the primary index's physical
registration and the entry-space format version. Everything else a relational
catalog carries (schemas, tables, columns, constraint descriptions) has no
key-value counterpart — the parent issue's design note ("do not hide catalog and
security semantics behind a raw dictionary metaphor") is honored by making the
catalog **explicit and small**, not by inventing relational ceremony for a model
that has none.

#### Catalog: Why-this-not-that

- **A dedicated catalog file set, not records mixed into the data file.** The SQL
  family precedent (a data set and a `catalog` set in the database's directory) holds: catalog writes are
  self-committing storage transactions with no MVCC layer above them, and mixing
  self-committing records into a data file whose journal carries logical
  transaction lifecycles would entangle recovery classification with metadata
  writes. Symmetry also keeps the engine's storage strategy identical to SQL's.
- **The `SqlCatalog` persistence pattern, reduced** (the former `DefaultSqlCatalog`,
  collapsed into `SqlCatalog` at phase 4 of the concrete-types plan). One tuple-codec record
  per concern (kind 1 = registrations, kind 2 = format version), rewritten in
  place when it fits and relocated when it grows. The registration codec matches
  `SqlCatalog`'s byte-for-byte so the family stays mutually legible; a
  shared registration-codec helper is deliberately not extracted yet (two
  instances, both trivial — the extraction threshold is the third model).
- **Format version named `EntrySpaceFormatVersion`, not `RecordSpaceFormatVersion`.**
  The key-value model was born on format 1 (MVCC-stamped entries in the key
  space's page chain, indexed by a tree of B-tree page format 1); the name scopes
  the marker to the model's own vocabulary. The marker describes the whole data
  file set, entry records and the primary index tree that rides it, so #1194's
  B-tree page format 2 (entries ordered by key, entry location and writer) made
  it 2 although the records did not change. The engine writes the marker at
  creation, before it registers the primary index, and refuses a database that
  registers one on any other version, older or newer; there is no upgrade
  machinery (owner decision of 2026-10-02; #1152). An absent marker reads as 1.
  Bumping the marker, rather than relying only on `Database.Indexing`'s own page
  stamp (which it checks on the tree's root when the engine attaches the
  registration this catalog persists), is what fences engines from before #1194:
  they reject a marker newer than 1 but cannot read the page stamp. The catalog
  itself is unchanged: the marker is an opaque integer here.

#### Catalog: Query-time introspection captures (C2)

The engine's `KEYSPACES` command describes the single implicit key space from
this catalog. `KeyValueCatalog.CaptureSnapshot()` returns a
`KeyValueCatalogSnapshot` containing the entry-format marker and index
registrations copied together under the existing metadata lock. The engine alone
projects those values into client rows; the catalog stores no virtual objects or
materialized discovery records. An existing capture is unaffected by later
metadata publications. `KeyValueCatalogSnapshot` is public because it is that
method's return type; it is a sealed class with an internal constructor, not a
record, so no caller can build or clone one. The capture owns no storage and
requires no disposal, and registrations are exposed through a read-only collection,
matching their existing catalog vocabulary rather than adding command-result types
here.

#### Catalog: One sealed type (concrete-types plan, phase 4, #1260)

`KeyValueCatalog` is one `public sealed` class. Until phase 4 the catalog was a
public `IKeyValueCatalog` interface, an internal `DefaultKeyValueCatalog` and a
`public static class KeyValueCatalog` whose `Open` returned the interface and whose
static `CaptureSnapshot(IKeyValueCatalog)` downcast to the internal type; the capture
was kept off the interface so a second catalog implementation would not owe it.
There is no second implementation: a key-value database has exactly one catalog,
over its own file set. So the three collapsed into the sealed class
(`.claude/rules/database-area.md`, rule 1), `Open` is its static factory over a
private constructor, and `CaptureSnapshot` is an instance method. The SQL catalog
gets the same shape in the SQL model's phase-4 PR.

The capture contains one database's metadata because this catalog is opened on
that database's dedicated file set. The engine omits physical index page ids and
exposes the command as read-only. Its two mutation verbs reject the `KEYSPACES`
target with `DatabaseParseException` and the stable message
`The KEYSPACES catalog surface is read-only.` Catalog mutation APIs continue to
serve internal persistence; they are not command operations. The model has no
schema ownership or named key-space records, so introspection invents neither.

#### Catalog: Error model

`KeyValueCatalogException : DatabaseException` — a malformed persisted record or
invalid metadata write. Catalog failures are database failures to callers.

#### Catalog: Non-goals

- Key-space registries (multiple named key spaces per database) — deferred; the
  engine currently owns one implicit key space (object id 1).
- TTL/expiration metadata — deferred with the model feature.
- Constraint descriptions — key uniqueness is enforced physically by the unique
  primary index; there is nothing to describe.

#### Catalog: AOT posture

Tuple-codec encode/decode only; no reflection, no serialization frameworks.


## Storage component

The Storage component is part of `Assimalign.Cohesion.Database.KeyValuePair`, with public types in `src/Storage/` and helpers in `src/Internal/Storage/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Storage: KeyValuePair storage design

#### Storage: Design intent

A thin model-storage component: it types a storage file set as
`StorageModel.KeyValue` and exposes the kernel's protected record surface under
entry-flavored names (`InsertEntry`/`ReadEntry`/`UpdateEntry`/`DeleteEntry`). The
key-value model deliberately brings **no model-specific physical layout** — the
generality bet of the second model is that the shared substrate (slotted pages,
per-owner record chains, WAL v2, recovery) serves an ordered key-value store
as-is, and this component is where that bet is visibly small.

#### Storage: Why-this-not-that

- **A `Storage` subclass, not a wrapper.** `SqlStorage` set the family shape: the
  kernel's record operations are `protected`, and the model component is the
  sanctioned way to expose the subset a model composes. A wrapper would duplicate
  the surface; a public kernel record API would invite models to bypass their
  engines.
- **No entry codec here.** The record byte layout (16-byte version-stamp header +
  key/value tuple) is engine policy, tied to the engine's MVCC composition — it
  lives with the version store and executor in `Database.KeyValuePair`, exactly
  where `SqlRowCodec` lives in `Database.Sql`. This component stays byte-agnostic.
- **Owner-chain inserts are the primary insert path.** The engine keys every entry
  record by its key-space object id so the key space owns its pages (the #911
  per-object chain design). The shared-space overload exists for the catalog file
  set, whose records are few and unowned.

#### Storage: Lifecycle

Instances are created/opened by the engine's storage strategy and disposed by the
engine's database instance. `Open(..., checkpointOnOpen: false)` is the reopen
contract: the engine runs `TransactionRecovery.Analyze` over the recovered journal
before any truncation (classification reads lifecycle records a checkpoint would
destroy) and checkpoints through its coordinator once analysis completes.

`WriteAheadJournal` publicly returns the storage's `StorageJournal` so a
transaction coordinator can share the storage's actual WAL. This is the same
composition seam exposed by Documents, Graph, and Blob storage and replaces the
shipped-to-shipped friend grant. Storage retains ownership of the journal; callers
coordinate writes and checkpoints through the transaction coordinator and do not
dispose the journal separately.

#### Storage: Non-goals

- Model-specific page layouts (adjacency pages, blob streams) — the key-value
  model needs none.
- Public exposure of the kernel's full record surface — only what the engine
  composes is exposed, including the journal contract required for transaction
  coordination. Paging, buffer management, and WAL implementation remain private.

#### Storage: AOT posture

No reflection, no codegen — inherited span-based record operations only.


## Public assembly identity changes

The following public types move from their Catalog or Storage assembly into Assimalign.Cohesion.Database.KeyValuePair. Their namespaces, members, diagnostic codes and serialized representations are unchanged. Public members whose signatures expose these types also reference their new assembly identity. Consumers must replace satellite project/package/assembly references with the model reference and recompile; assembly-qualified type names change. No type forwarding or compatibility assembly is supplied. Empty Security (and Cache Catalog/Storage) projects had no public types.

- From `Assimalign.Cohesion.Database.KeyValuePair.Catalog`: `Assimalign.Cohesion.Database.KeyValuePair.Catalog.KeyValueCatalog`, `Assimalign.Cohesion.Database.KeyValuePair.Catalog.KeyValueCatalogException`, `Assimalign.Cohesion.Database.KeyValuePair.Catalog.KeyValueCatalogSnapshot`.
- From `Assimalign.Cohesion.Database.KeyValuePair.Storage`: `Assimalign.Cohesion.Database.KeyValuePair.Storage.KeyValueStorage`.
