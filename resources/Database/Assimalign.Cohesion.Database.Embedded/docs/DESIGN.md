# Assimalign.Cohesion.Database.Embedded — Design

## Intent

The Database area is the data layer for the rest of the Cohesion platform (area `DESIGN.md` R10). Most platform resources should not run — or depend on — a separate database server process for their own state; they embed the engines they need. This project is that consumption path, and it is deliberately thin.

## The engine self-sufficiency principle

The facade can only be thin because of an invariant this project *enforces by existing*: **engines are self-sufficient libraries**. An engine owns its internal background workers — WAL flushing, checkpointing, version pruning — whether it runs embedded or hosted. `Database.Hosting` merely composes the per-model wire-protocol servers that front engines; it adds no behavior an embedded consumer would lose (since 2026-07-13 the engine's worker loops are engine-internal, always — there is no host scheduling to miss). If an engine ever requires the host to function, embedded consumers break — that is a design defect in the engine, not a missing feature here.

## Decisions

- **Composition only.** `EmbeddedDatabase` registers engines, looks them up by name or model, and disposes them in reverse registration order. It does not proxy engine operations — consumers work with `DatabaseEngine`/`DatabaseInstance` (or a model's sealed leaves, `SqlDatabaseEngine` and the rest) directly, so embedded and hosted code paths stay identical.
- **No DI.** Repo rule: `*.Hosting` is the only DI seam. Embedded consumers new up engines from their factories (`{Model}DatabaseEngine.Create(name, options)`, or the model's engine builder); resources with DI wire this in their own hosting layer.
- **Engine-name uniqueness enforced at composition**, ordinal-ignore-case, matching `DatabaseEngine.Name` semantics elsewhere.
- **Typed over the root engine base (concrete-types plan, phase 6, #1262).** `EmbeddedDatabaseOptions.Engines`, `EmbeddedDatabase.Engines` and both `TryGetEngine` overloads name `DatabaseEngine`, the root base every model engine derives from; the `IDatabaseEngine` interface they named is deleted (plan §5.2). `TryGetEngine`'s engine is `[MaybeNullWhen(false)]`: null when the lookup fails. A caller that knows the model casts the engine it gets back to the model's sealed engine.
- **Best-effort disposal with aggregation.** One failing engine must not leak the others' file handles; failures are collected and rethrown as `AggregateException`.

## Engine roots

Embedded receives engines whose model constructors have already prepared their roots through
DatabaseRootDirectory. It adds no separate root or file policy. Several models may share one root.

A new Unix root is created with mode 0700. An existing root is admitted unchanged when it has
no group write and no access to others. The setuid, setgid and sticky bits are not checked, so
02700, 02750 and 01700 are admitted, including a new root under a setgid parent. A wider root
is changed to exactly 0700 only when it holds only engine data. One model Warning event,
EngineRootPermissionsTightened, reports the absolute path and old/new octal modes after the change.

Engine data includes lower-case storage-name directories holding any model's data file:
sql.dat, keyvalue.dat, document.dat, graph.dat or blob.dat. An empty storage-name directory or
one holding only interrupted file sets is also recognized: those five sets' .dat/.log/.bak files,
catalog.dat/catalog.log/catalog.bak and sql.create. The default .cohesion-temp directory is
recognized by name in every model. Sql also recognizes its configured temp directory and
ancestors under the root, using Path.GetRelativePath's platform comparison; the other four
models have no configurable temp directory.
.DS_Store files are recognized. Other root entries, including reparse points, are foreign.

A foreign entry refuses construction with DatabaseException and structured CK06007. The message
names the first blocking entry and says the engine will not change the mode of a directory holding it. Run
`chmod 700 "<path>"` if the directory belongs to this engine, or point RootPath at a new subdirectory,
which the engine creates with mode 0700. A failed mode change uses the same code, retains its cause
as the inner exception, and says this account cannot change the root's mode. Point RootPath at a
new subdirectory, or make this account the directory's owner and run `chmod 700 "<path>"`. A failed
inspection has its own message naming the directory that could not be listed: the engine
cannot confirm that the root holds only engine data, so it will not change the root's mode.
It also uses CK06007, retains the I/O cause as the inner exception, and gives the same remedy.

Only the root mode is checked; child directory/file modes retain process defaults. Inspection
and the change both work on the path, leaving a check-then-change window; the change can only
narrow a mode. The mask agrees with PostgreSQL src/include/common/file_perm.h:29, applied at
src/backend/utils/init/miscinit.c:354. Two differences remain: the engine can tighten a wider
root holding only engine data instead of refusing it, and it does not check the owner's user ID
as PostgreSQL does at miscinit.c:334-339, because .NET has no managed API for that ID.

On Windows missing parents are created with Directory.CreateDirectory and inherit normally.
Only a new root receives a protected ACL granting inheritable full control to the current user,
SYSTEM and BUILTIN\Administrators. Existing Windows roots are used unchanged. The helper uses
guarded framework APIs without reflection. For admitted roots, temporary-space rules and
open/create/drop behavior are unchanged; persisted formats are unchanged.

## Non-goals

- No cross-engine transactions — a transaction is scoped to one database in one engine.
- No configuration binding here — `cohesion.config` binding belongs to the consuming resource's hosting layer.
- No lifecycle states beyond compose/dispose — engines own their own `EngineState`.

## AOT posture

Pure composition; no reflection, no discovery. Consumers reference engine packages statically.

## Phase 29 root-contract migration

The embedded test engine now implements the root's `DatabaseName` operations and
read-only `Servers` observation; since phase 6 of the concrete-types plan it derives from the
root `DatabaseEngine` base and implements only its protected cores (including the explicit,
non-generic creation-state parameter), its disposal core recording
the disposal order (`database-area.md`, "Test doubles"). Embedded composition continues to own the engines
explicitly supplied to `EmbeddedDatabase`; this existing aggregate is separate
from the hosting builder's instance-borrowed registration contract.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Only stored fields and reviewed pure getters are projected; inspection does not execute queries, enumerate results, invoke clocks or mutate lifecycle state.
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.
