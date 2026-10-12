# Assimalign.Cohesion.Database.Sql.Catalog — Design

The SQL model's schema authority (area architecture:
[resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.3). The catalog answers exactly
two questions for the planner: *what objects exist* (with stable identities) and
*what shape are they* — and it must answer identically after any crash.

## Durable pending migrations (49a)

Catalog kind 9 is a chunked UTF-8, source-generated JSON `SqlCatalogPendingMigration` envelope.
It carries the complete target document/hash, ordered immutable step identities and their
one-based positions after inline steps. It uses the same strict chunk assembly and one storage
transaction as applied-schema records (kind 6). The SQL engine gates this addition with data
format 9; no legacy reader or upgrade path is added. Persisting intent does not advance DDL
generation or replace the old applied marker. Saving the completed applied marker deletes every
pending chunk in the same transaction. Open validates the envelope and ordering, and the engine
checks the target document's hash before resuming. Catalog persistence knows no SQL executor,
worker, or hosting lifecycle.

## Collation metadata (#1025)

`SqlCatalogColumn.Collation` is an optional string-column override. Null inherits
`SqlCatalog.DefaultCollation`, which is Binary when no default record exists.
The default is persisted in a dedicated kind-7 catalog record and captured in
immutable statement snapshots.

`DefaultCollation` is read-only. The default is established when
the catalog is opened — `SqlCatalog.Open(storage, defaultCollation)` — and is
fixed for the lifetime of the database, because every index key on a column that
inherited it is encoded through that collation's byte transform. Opening an
already-populated catalog under a different default is rejected; reopening under
the same one, or with no default supplied, keeps the persisted value.

Making this creation-time state rather than a mutator is deliberate: a setter
would tell every caller the value is changeable and then guard that promise at
runtime.

Table metadata extension version 2 appends one collation identifier per column
after the existing constraints. Version-1 and pre-extension records still read
with null column overrides and the Binary database fallback. Column additions,
drops, and restart preserve explicit overrides. An index inherits its key
columns' effective collations; the SQL engine verifies index eligibility and
uses those same transforms for uniqueness enforcement.

## Physical column layout and dropped columns (#1241)

A table record describes the physical layout its rows are stored in, not just its
columns. `SqlCatalogTable.Columns` are the live columns, in order: what every name,
`SELECT *`, INSERT without a column list and the system views see. A stored row
version holds one component per *physical ordinal*, up to its last live column (the
SQL engine stores nothing for a dropped ordinal behind it); `PhysicalColumnCount` is
their number, `DroppedColumnOrdinals` lists the ones whose column was dropped (ascending),
and `GetPhysicalOrdinal(i)` maps a live column to its component. The live columns
take the non-dropped ordinals in order, so the layout is fully described by the
dropped list.

- **DROP COLUMN marks; it never renumbers.** `DropColumnAsync` removes the column
  from `Columns` and appends its physical ordinal to `DroppedColumnOrdinals`, in the
  same single-record, self-committing replacement as every other alteration. No
  other column's ordinal changes, so the SQL engine rewrites no row: a stored
  version keeps the dropped component, which every decode skips. This is
  PostgreSQL's `pg_attribute.attisdropped`
  (`src/include/catalog/pg_attribute.h:139-140`, set by `RemoveAttributeById`,
  `src/backend/catalog/heap.c:1692-1732`). The guards are unchanged: an unknown,
  primary-key, constrained, referenced or indexed column, or the last live column,
  cannot be dropped.
- **ADD COLUMN appends.** `AddColumnAsync` gives the new column ordinal
  `PhysicalColumnCount`, after every live and dropped ordinal (PostgreSQL:
  `relnatts + 1`, `src/backend/commands/tablecmds.c:7445-7446`), so a column
  re-added under a dropped column's name never shares the dropped column's
  component.
- **Ordinals are never reclaimed.** Reusing a dropped ordinal would decode the
  dropped value, still stored in older versions, into another column. PostgreSQL
  keeps a dropped attribute's number for good and counts it against its column limit
  (`doc/src/sgml/limits.sgml:133-134`); here each dropped ordinal costs the table
  record one five-byte integer component and counts against the one-record limit
  below. When a table that has dropped columns outgrows the record, the refusal
  counts them and says that recreating the table and copying its rows reclaims them,
  since only a new table starts without dropped ordinals (`EncodeTable`; a
  two-column table reaches the limit after 1,588 ADD/DROP cycles).
  Compacting the layout would take a table rewrite that moves every row version and
  rebuilds its indexes; none exists, and one added later must replace the layout in
  the same recoverable unit as the versions (the SQL engine's DESIGN, "DROP COLUMN
  marks the column dropped").
- **Every alteration carries the layout.** ADD/DROP COLUMN and ADD/DROP CONSTRAINT
  copy the dropped ordinals into the replacement, and `PublishTableAsync` refuses,
  with `SqlCatalogException`, a replacement that does not keep the current layout as
  its prefix (the same dropped ordinals, every existing live column in place by
  name; it may only append) and a new table published with dropped columns. A
  layout change is the one catalog mistake that corrupts every read silently, so the
  catalog checks it rather than trusting each caller.
- **Persistence: table extension version 3.** The table record's trailing extension
  (constraints in version 1, column collations from version 2) appends the dropped
  ordinals' count and values from version 3. Versions 1 and 2 still load, with no
  dropped column, so that the SQL engine's data-storage format gate (format 6 for
  this layout) refuses a database written before it with its own message rather than
  a decode error. A persisted layout that cannot describe the table (a negative
  count, an ordinal outside the physical columns, a repeated ordinal) fails the load
  with `SqlCatalogException`.

## A column's domain (phase E3)

A column declared with a domain (engine-registered through `SqlType.CreateDomain`, owner
decision 68, or persisted through E4's `CREATE DOMAIN`) stores the domain's base type:
`SqlCatalogColumn.Type` is the base type with its
length, precision and scale, and `SqlCatalogColumn.DomainName` the domain's name. The
column record keeps the name only. Engine-registered domains remain code; SQL-defined
domains are catalog objects in kind-11 records. On reopen the engine binds column names
against the merged engine registrations and persisted SQL domains. An unavailable domain
leaves the database readable but refuses writes assigning that column with `COHSQLE010`.
PostgreSQL records a domain column's type as the
domain's own OID and keeps the domain in `pg_type`; here the base type is what the row
codec, the key encoding and every comparison read, so they do not consult the domain.

- **Every alteration carries the name.** ADD/DROP COLUMN and ADD/DROP CONSTRAINT copy the
  column instances, and the SQL engine's planner passes the domain name when it rebuilds a
  column (the primary key's implicit NOT NULL).
- **Persistence: table extension version 4** (data-storage format 7). After the dropped
  ordinals, one optional string per live column, NULL for a column of a built-in type,
  which this catalog always writes. Versions 1 to 3 still load, with no domain, so that the
  format gate refuses a database written before it with its own message. A persisted blank
  name fails the load with `SqlCatalogException`, as does a blank `domainName` at the
  column's constructor (`ArgumentException`).

## Why-this-not-that decisions

- **A dedicated catalog storage file set** — not catalog rows mixed into the data
  file. The current row facade exposes one record space per storage instance;
  sharing it would put metadata records inside user table scans. A second
  `SqlStorage` (the `catalog` file set in the database's directory) buys complete isolation while
  reusing the exact same durability machinery — no bespoke metadata persistence.
  When the data file gains per-object record spaces, folding the catalog back in
  becomes an option, not a requirement.
- **Self-committing DDL.** Every catalog mutation runs in its own storage
  transaction and is durable (WAL commit record) when the call returns. DDL inside
  a user DML transaction is deliberately unsupported in the MVP: the in-memory
  cache updates on commit, and half-visible schema changes are a correctness trap
  (historically mishandled by real engines). The engine locks affected tables and
  the catalog serializes their metadata publications.
- **The shared tuple codec as the record format.** Catalog records are encoded
  with `DatabaseKeyWriter`/`DatabaseKeyReader` (#854): self-describing, exact
  round-trips, one codec maintained in one place. Ordering (the codec's other
  property) is irrelevant here — reuse beats a second bespoke format.
- **One record per table.** Columns and the primary key fold into the table's
  record: schema changes rewrite one record (in place when it fits, relocating —
  delete + insert — when it grows). Per-column records would buy nothing at this
  scale and cost multi-record consistency. A record lives in one slotted-page slot,
  so a table definition, an index description or the registration set is limited to
  `SlottedPage.MaxRecordSize` (8,092 bytes) encoded. The encoders check that limit
  before storage is touched and throw `SqlCatalogException` naming the object and both
  sizes; the definition in memory and on disk stays the last one that fit (#1157
  review — it used to surface as a raw `SlottedPageException` from the relocation's
  insert). Definitions larger than a page wait for overflow records.
- **Object identities are catalog-assigned `ulong`s** persisted with a counter
  record, monotonic across reopen (the loader also raises the counter past every
  loaded table, so a torn counter update can never recycle an id). Data rows,
  index registrations, and lock resources key off these ids.
- **Index directory persistence lives here** — the index manager stays a physical
  component (`Database.Indexing`'s documented split): the catalog stores the
  exported `BTreeIndexRegistration` set and hands it back for re-attachment on
  open. A tree's root page stays fixed through splits (#1159), so registrations
  change only through index DDL. Checkpoint and shutdown persist the already
  committed catalog; they never export or save another registration snapshot.
- **Index descriptions are schema metadata, one record per index (kind 5)** —
  `SqlCatalogIndex`: name (unique per table, case-insensitive), owning table
  object id, ordered key columns, uniqueness. The description is deliberately
  separate from the physical registration: the description is schema, the
  registration physical identity, and the planner needs columns/uniqueness the
  registration doesn't carry. **Description and registration writes are atomic** —
  `CreateIndexAsync`/`DropIndexAsync` take the registration set and persist both
  records in one self-committing transaction, because a crash must never leave a
  description promising an index no tree backs (an unenforced UNIQUE) or a
  registration re-attaching a tree no statement can reach. `DropTableAsync`
  removes the table's descriptions and registrations the same way. Guards:
  dropping an indexed column is rejected (entries key on its values); index
  columns must exist at creation.

- **The record-space format version lives here** (kind-4 record, kind 8 from
  version 4 as below; `RecordSpaceFormatVersion`): data rows are not self-describing across layout
  changes — a stamped (MVCC, version ≥ 2) record and an unstamped (version 1)
  record cannot be told apart record-by-record, version 2 vs 3 (shared page
  stream vs per-object page chains) is a page-placement property no record
  carries, and version 3 vs 4 (index keys with or without the temporal kind and
  offset, #1099) and 4 vs 5 (index trees in B-tree page format 1, ordered by key
  alone, or format 2, ordered by key, entry reference and writer, #1194) are
  properties of the index trees, and 5 vs 6 (whether rows are decoded through
  the table records' physical layout, #1241) is a property of these records
  themselves — so the database-grain marker is catalog
  metadata. (`Database.Indexing` also stamps and checks its own page format on
  each tree, behind this marker.) The engine writes its version (10) when it
  creates a database and refuses to open one whose marker reads anything else; it
  has no upgrade path (owner decisions of 2026-10-01 and 2026-10-02; upgrades are
  #1152). Versions 5 through 10 are stored as a kind-8 record like version 4. Absent marker
  reads as version 1 (pre-marker databases, or a creation interrupted before the
  engine stamped the marker).
- **From version 4 the marker is a kind-8 record (downgrade fence, #1099).**
  Catalogs before format 4 (through 10.0.0-preview.1) load kinds 1–7 and throw
  `SqlCatalogException` on any other kind, but their engines did not refuse a
  marker newer than they understood. Left at kind 4, a format-4 marker would
  let such an engine open the database and write format-3 temporal keys into
  it, and the newer engine would trust those keys because the marker still
  reads 4. `SetRecordSpaceFormatVersionAsync` therefore rewrites the single
  marker record in place as kind 8 for any version of 4 or more (and as kind 4
  below that); `Load` reads either kind. An older engine now fails the open
  with "Malformed catalog record of kind 8" instead of corrupting the indexes;
  downgrade is unsupported.
- **The applied compiled-schema state lives here** (kind-6 records). The catalog
  stores the lowercase content hash and DDL generation together with the complete
  canonical schema document. Documents are strict UTF-8 and chunked into bounded records; replacing
  all old chunks with all new chunks is one self-committing storage transaction,
  so reopen observes either complete state. Equal declared hash and unchanged DDL
  generation let the SQL provisioner skip in constant time. Any later DDL invalidates
  that proof and the provisioner reconciles from live schema-owned metadata.

## Concurrent index publication (49a review)

Tree builds on different tables can finish before either publishes. Their exported
registration snapshots are therefore not authoritative replacements for the directory.
Under the catalog metadata lock, `CreateIndexAsync` and `PublishTableAsync` merge only
the registrations for the new descriptions they publish into the current directory.
`DropIndexAsync` removes only the named index from that current directory. Table drops
already remove only that table's registrations. Each merged directory is committed with
its descriptions, so an unrelated publication cannot erase a completed tree's durable
registration. PostgreSQL likewise inserts the affected `pg_index` tuple rather than
replacing an independently captured directory (`src/backend/catalog/index.c:669`).

Every new index description requires exactly one physical registration in that same
publication, matching the table identity, case-insensitive index name, and uniqueness,
with B-tree kind and a positive root page id. Missing, duplicate or inconsistent
registrations fail before any metadata or DDL generation changes. Both standalone
index creation and table publication use this validation. Unrelated registrations
in the snapshot are ignored, preserving the current directory.

Description-first publication and the separate registration-save API are removed.
There is no production caller needing separate publication. A delayed snapshot cannot
identify an index incarnation from its table, name and uniqueness: after DROP W on
OldValue and CREATE W on NewValue, those fields still match the abandoned tree.
Accepting that tree into a missing registration slot would make the replacement seek
old keys and stop enforcing its own unique values. Requiring atomic publication closes
that gap without a new incarnation field or an on-disk format change. Roots stay fixed
through splits, so checkpoint and shutdown need no registration-save backstop and
cannot overwrite a newer tree or resurrect a dropped one.

`SqlIndexPublicationTests` builds real trees, captures disjoint snapshots before
publication, commits them in the conflicting order, and opens durable crash images.
It also rejects description-first same-name recreation, checks that rejection survives
reopen without advancing DDL generation, and proves the atomically published replacement
seeks its new key and rejects duplicates after checkpoint and reopen. An unrelated
publication carrying the dropped tree's stale registration cannot change that result.
Catalog tests reject missing, ambiguous and invalid registrations through both APIs,
including new and replacement tables, with unchanged live and reopened metadata.
These are ordered interleavings, with no scheduler-dependent race window.

## DDL generation and crash convergence (B2)

The kind-2 identity counter also persists `SqlCatalog.DdlGeneration`, a monotonically
increasing `ulong`. Every committed metadata change writes the next generation in the
same storage transaction: table identity reservation and publication, direct table
creation and drop, column and constraint changes, index creation and drop,
default-collation initialization and format-marker changes.
Schema-owned and ad-hoc objects follow the same path. `CommitCatalogChange` stages the
counter with the change and publishes the generation, identity and record locations
only after commit succeeds. A failure before the commit record leaves the transaction
active; disposal rolls it back, preserving the committed generation and allowing a retry.
A failure after that record takes the storage offline through the kernel's existing
fail-stop latch and reports an unconfirmed commit. SQL's paired-storage hooks take the
whole database offline and end lock waits. Cached catalog reads and mutations are refused:
the identity, generation and record locations may be stale. Reopen recovers the actual
outcome before another mutation can allocate an identity or generation. This is the same
rule PostgreSQL applies in `RecordTransactionCommit`'s critical section
(`src/backend/access/transam/xact.c:1470-1583`) and on WAL fsync failure
(`src/backend/access/transam/xlog.c:9877-9937`). Exhaustion refuses the mutation rather
than wrapping.

Saving an unchanged format marker is a no-op. Checkpoint and shutdown never change
index registrations or DDL generation, so neither invalidates a fully applied schema. Recording
`SqlCatalogSchemaState` is bookkeeping, not DDL: its `{hash, generation}` describes the
catalog after every provisioning step succeeded and does not advance the counter.
`SaveSchemaStateAsync` requires that supplied generation to equal the live generation
under the metadata lock, so intervening DDL cannot be acknowledged accidentally. Every
state chunk carries the same hash and generation; load refuses mixed generations.
An unconfirmed state save follows the same fail-stop path, so another save cannot
reuse the previous chunk locations after a replacement may already have committed.

This changes the on-disk catalog shape and advances the SQL database's existing
record-space format gate from 7 to 8. Counter and schema-state records require the
generation suffix; older records fail with an explicit unsupported-format error before
they can be trusted. There is no upgrade or compatibility reader. After such a load
failure, the SQL engine reads only the unchanged format-marker tuple and sends a
different recorded version through its existing format gate, preserving its actionable
refusal over the wire. A missing marker together with exactly one valid old counter
tuple identifies the pre-marker format 1 and reaches that same refusal. This only
recognizes the old shape to refuse it; it does not load an older catalog. A current,
malformed or ambiguous marker, duplicate counter or unknown record kind preserves
the original catalog error; successful opens require no additional traversal.

There is no transaction across provisioning statements. A crash after a committed step
leaves the previous recorded generation behind the live counter. The SQL provisioner
then takes a complete snapshot of owned objects and plans only the remaining work;
the state is recorded last. The catalog keeps types and their bounds, domain names,
nullability, canonical defaults, primary keys, unique and foreign-key constraints,
checks and indexes so that reconciliation can represent each provisionable kind.
Engine-registered domains remain code and are represented by their column references.

Owner decision 49a's pending-step record attaches between planning and step execution
in the SQL provisioner. Successful deferred DDL uses the same catalog commit path, and
the applied state is recorded only after all steps complete. The pending envelope lives
here; worker execution and progress remain SQL engine responsibilities.

## Object ownership

Table and secondary-index records persist `DatabaseObjectOwner` and the owning
compiled schema's name. `SqlCatalogTable.Schema` is strictly the SQL namespace
(for example, `dbo`); `SqlCatalogTable.OwningSchema` is the compiled schema that
provisioned the object and is null for ad-hoc objects. `SqlCatalogIndex` exposes
the same ownership value as `OwningSchema`, distinct from its table's SQL
namespace. Ordinary catalog creation defaults to `Adhoc`; the SQL provisioner's
`SqlCatalog.ReserveTableAsync`/`SqlCatalog.PublishTableAsync` path stamps `Schema` ownership and `OwningSchema`
atomically with the first table record. Index creation already accepts a complete
description and persists the same metadata. Column add/drop replacements retain
the table's ownership unchanged.

The ownership fields are an appended tuple suffix. Older records without the
suffix load as `Adhoc`, preserving their existing mutability rather than guessing
an owner from a database-wide schema marker. Invalid owner/name combinations are
rejected. The tuple encoding is positional and never persisted CLR property
identifiers; renaming the ownership property to `OwningSchema` therefore does not
change the on-disk format and needed no compatibility alias. B2's format gate now
refuses those older files. The schema hash/document record now also carries the
B2 generation and continues to support drift detection independently of per-object ownership.

The engine enforces the live-session DDL lock; the catalog remains the durable
metadata component used by sanctioned schema application as well. Ownership
metadata is accepted by the staged-publication members of `SqlCatalog`
(`ReserveTableAsync`, `PublishTableAsync`); authorization to change an existing
schema-owned object remains an engine/session decision. The older direct-creation
helpers (`CreateTableAsync` with ownership and constraints, `AddConstraintAsync`)
remain internal and are used only by catalog tests.

## Constraint persistence

Table records now append a versioned constraint extension after ownership: version
`1`, constraint count, and each immutable foreign-key/check definition. A reference
stores its ordered local and target columns, target SQL namespace/table, and
`RESTRICT` or `CASCADE` delete action. A check stores its predicate as canonical
SQL text (below). Records ending after the original primary keys or ownership
suffix still load with no constraints. Unknown extension versions and malformed
definitions fail closed.
Column changes retain constraints, and add/drop constraint rewrites use the same
WAL-backed, self-committing record replacement as existing catalog metadata.

`UNIQUE` uses `SqlCatalogIndex.IsUnique`, matching `CompiledSchemaIndex.IsUnique`;
it does not acquire a competing foreign-key/check constraint kind. Creation of a
table with unique declarations reserves and durably advances its object identity,
then the engine commits the empty index trees before publishing the table,
constraints, index descriptions, and registrations in one catalog transaction.
A crash before publication leaves no visible table with missing enforcement.
Replacement publication similarly commits newly added columns/constraints and
their new indexes together. `SqlCatalog.ReserveTableAsync` and
`SqlCatalog.PublishTableAsync` expose this composition lifecycle. A reservation
persists only the identity counter and does not lock the name; callers serialize
DDL and durably build enforcing indexes before publishing. New publications reject
zero or unallocated identities so they cannot bypass the durable identity counter.
Replacement publication
retains existing index descriptions while adding the supplied new descriptions.
`SqlCatalog.DropConstraintAsync` owns removal of persisted foreign-key/check
metadata, alongside the catalog's table, column, and index mutations.

Index records have their own version-`1` trailing extension carrying `IsPrimaryKey`.
This identifies the physical index enforcing primary-key metadata, allowing schema
reconciliation to distinguish it from a separately declared unique index on the
same columns. Older index records have no marker and load as ordinary indexes.

## Persisted SQL expressions are canonical text

Every SQL expression this catalog stores — table CHECKs, column literal and function-call
DEFAULTs, domain CHECKs and literal DEFAULTs, and SQL function bodies — is
**canonical SQL that the engine rendered from the parsed
tree** (`SqlExpressionRenderer`, Sql.Language), never the text a user wrote. A check
declared as `QTY>0   and qty<100` is stored as `QTY > 0 AND qty < 100`; a default
declared as `'it''s'` or `+5` is stored as `'it''s'` or `5`, the SQL literal rather
than its bare value. New persisted expressions must follow the same rule; storing
source text is not an option, because a stored definition must mean the same thing
under every later parser. Future general expression defaults and view queries use this
same path; CREATE TABLE function-call defaults are already supported in format 10.

The catalog itself stays SQL-agnostic: it has no reference to the language package,
stores the text as an opaque string in the same tuple fields as before, and validates
only shape (a check needs non-blank text; a reference needs its columns). The SQL
engine owns the rule end to end. It renders and verifies the canonical text before
publishing a definition, and when it opens a database it parses and binds every
stored table check and default once per table version, and reconstructs SQL functions
and domain definitions on open. A stored
definition that does not load fails that open, naming the table and the constraint or
column, instead of failing a later write; DDL inside the engine never stores text
that would not reload. Opening binds a stored definition (its columns resolve, and it
is something the engine can evaluate) without re-applying the rules DDL uses to accept
one, so a later release that narrows those rules never makes a stored definition fail
the open. Canonical storage is part of data-storage format 4, so it needed no further
format bump; text stored by earlier formats is not migrated, and the engine refuses an
earlier-format database whose catalog holds a check or default with a format error. The
engine's [persisted-definition design](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#persisted-definitions-canonical-text-parsed-once)
has the details.

## Single source for SQL system views (C1)

The engine's `INFORMATION_SCHEMA` and `COHESION_SCHEMA` relations project this
catalog's table, column, key, index, constraint, and ownership descriptions.
Those descriptions remain the single source of truth: system views are computed
when queried and never inserted as stored catalog tables or copied into a second
metadata store. `SqlCatalogTable` describes stored objects only and gains no
virtual/system flag. SQL view names, columns, binding, and row projection belong
to the SQL engine, not to this persistence library.

`SqlCatalog.CaptureSnapshot()` returns a `SqlCatalogSnapshot` containing
an atomic capture of tables, index descriptions, and default collation under the
catalog's metadata lock. `SqlCatalogSnapshot` is a public sealed type with an internal
constructor, the return type of a public member; callers can retain
the read-only capture without holding a storage handle or disposing it. Table
columns, primary-key columns, and index key-column names are copied into read-only
collections when descriptions are created; callers cannot mutate retained input
lists to alter a published table or an already captured directory. The SQL
session captures it at transaction begin for snapshot isolation and at statement
start for `ReadCommitted` or auto-commit. The engine derives every view row and
referenced constraint from that capture, preventing an enumeration from mixing
metadata before and after a DDL publication. Captures expose existing catalog
descriptions, with no SQL view binding, query execution, or mutable persistence
capability. These `SqlCatalog` members replace a shipped-to-shipped friend grant.
After a table drop, fresh snapshots contain none of its table, index, constraint,
column, or ownership metadata. See the SQL engine's
[virtual relation design](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#virtual-system-relations-c1)
for the query surface and the two deliberately non-standard extension views.

## One sealed type (concrete-types plan, phase 4, #1260)

`SqlCatalog` is one `public sealed` class, and `SqlCatalogSnapshot` another with an
internal constructor. Until phase 4 the catalog was a public `ISqlCatalog`
interface, an internal `DefaultSqlCatalog` and a `public static class SqlCatalog`
whose `Open` returned the interface and whose statics (`CaptureSnapshot`,
`ReserveTableAsync`, `PublishTableAsync`, `DropConstraintAsync`) took the interface
and downcast to the internal type, so a second catalog implementation would not owe
them; the snapshot was likewise an `ISqlCatalogSnapshot` interface over an internal
class. There is no second implementation: a SQL database has exactly one catalog,
over its own file set. So the three collapsed into the sealed class
(`.claude/rules/database-area.md`, rule 1): `Open` is its static factory over a
private constructor, and the four statics are instance members with the same
checks and documentation. The internal test helpers (`CreateTableAsync` with
ownership and constraints, `AddConstraintAsync`) became internal instance members
the catalog's tests reach through the existing Sql.Catalog → Sql.Catalog.Tests grant.
No behavior changed. The `Abstractions/` folder is gone.

## Error model

`SqlCatalogException : DatabaseException` for catalog violations (duplicate or
missing tables/columns, primary-key drops, malformed persisted records).

## Non-goals

- Stored user-defined views, sequences, permissions (permissions are
  `Sql.Security`'s feature, #177). Virtual system views are engine projections
  of this catalog and require no view records here.
- Multi-statement DDL atomicity (see self-committing DDL above). The migration
  layer compensates completed reversible statements on failure; an MVCC bracket
  spanning catalog and data DDL requires a future catalog batch-transaction seam.

## AOT posture

Value/data classes, the shared codec and source-generated System.Text.Json metadata; no reflection-based serialization.

## E4 definitions and script receipts

The engine stamps format 10, previously 9. Kinds 10/11 store immutable SqlCatalogFunction and
SqlCatalogDomain records: signatures/types, canonical SQL, volatility/null behavior, dependency
identities and Adhoc/Schema ownership. Publication/removal advances DDL generation. Snapshots
include both kinds. The engine owns semantic binding and dependency/ownership admission.
Native function ownership is resolved by the requested normalized signature; an unrelated
engine overload does not turn a missing SQL definition into an engine-owned one.

Kind 12 stores SqlCatalogScript: case-sensitive name, exact-body SHA-256, statement count and
committed prefix, initial/latest DDL generations, typed schema hash and created table/index
identities. Kind 13 stores the ordered set's digest and reconciled generation. Tuple payloads use
source-generated System.Text.Json metadata with unknown-member refusal; no reflection serializer.
Definitions and receipts must fit the existing one-page record bound. The declaration's body stays
in application code, not the receipt.

Publication methods accept an optional explicit `SqlCatalogScript` receipt. The catalog stages
it with that operation's metadata and generation in the same transaction. There is no ambient
receipt switch or disposable scope. The receipt must describe the next incomplete statement;
reusing a completed receipt for another publication is refused. Only `ReserveTableAsync` marks
its commit as a reservation and retains the prior prefix; public `CreateTableAsync` publishes
the table and completes its receipt even though it also advances the identifier counter.
`SaveScriptAsync` records progress after data/read statements, whose atomic
DML receipts the engine owns. SaveScriptStateAsync checks generation before saving the O(1)
marker. No multi-statement catalog transaction was introduced. History is forward-only.

The engine preflights declared scripts against the typed schema before opening files: scripts
cannot ALTER/DROP typed tables or DROP typed indexes (`COHSQLS005`). Script-created tables may
evolve through later scripts, and script-created indexes on typed tables remain separately
tracked. The typed schema document therefore stays an accurate record of its own objects when
the engine advances its generation after a script commit.
