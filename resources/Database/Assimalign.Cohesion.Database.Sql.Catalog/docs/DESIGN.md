# Assimalign.Cohesion.Database.Sql.Catalog — Design

## Fixed-length characters (#1437)

Column DatabaseTypeInfo persists Character (19) and its length instead of String for CHAR(n).
Domain base types and function parameter/result type text retain CHAR(n), and rebinding after open
restores padding and comparison semantics. String and Character both accept collations.
SQL record-space format **12** supersedes 11 because CHAR index equality now ignores trailing
spaces. Catalog records retain their existing shapes; no old-format reader is added.
Applied/pending compiled documents use **cohesion/database-schema/v7**, shared with the SDK;
v6 and earlier documents are refused before hashes are compared. Rebuild artifacts and recreate
databases. INFORMATION_SCHEMA.COLUMNS reports CHARACTER with length; the COHESION_SCHEMA type,
domain and function views expose CHAR(n) through their existing name/signature columns.

The SQL model's schema authority (area architecture:
[resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.3). The catalog answers exactly
two questions for the planner: *what objects exist* (with stable identities) and
*what shape are they* — and it must answer identically after any crash.

## Durable table reclamation (decision 77, #1443)

SQL format 11 (formerly 10) adds catalog kind **14**: a typed tuple containing kind Int32,
retired table object id Int64, schema and table name as Binary-collated strings. DROP writes
one intent atomically with deleting the table/index descriptions, registrations, advancing the
DDL generation and (for declared scripts) recording the statement receipt. Its work is constant
in data-page count. The intent occupies a page owned by the retired id in the catalog file set;
normal metadata uses owner zero. Maintenance can therefore remove it without modifying a live
catalog page. Load rejects duplicate retired identities and an identity both live and retired,
and advances the identity allocator past every pending id. Reusing a name never reuses its id.

The catalog retains each decoded intent's identity and record location at open, adds it only after
the DROP commit, and removes it only after the retirement commit, all under its metadata lock.
`GetPendingTableReclamations()` returns an independent read-only snapshot of the committed identities;
`CompleteTableReclamationAsync()` idempotently retires an identity without advancing the DDL
generation. Record locations and the kind-14 codec stay private to this assembly. These two concrete
catalog members are the narrow boundary change requested in review #1443; no interface, friend grant,
or dependency is added. An unconfirmed retirement commit fences cached reads until reopen, just like DDL.

The SQL version-purge worker discovers identities after Build from that snapshot, releases the
catalog lock before allocating reader fences or reclaiming data, waits for older transactions/statements,
and frees owner-tagged data/index pages in bounded durable batches. It never walks mutable catalog
pages: a storage iterator's pin does not protect a record against ordinary deletion and page reuse.
Only after data durability does it ask the catalog to retire the intent. Recovery restores free headers and the FSM
before resumption: no cursor or cross-file atomic commit is required. An intent with no remaining
pages is harmless and completes on retry. Catalog itself never walks or frees the data file.
The #1443 review kept record-space format 11 unchanged. #1437 advances it to 12 for CHAR, as described above.

## Durable pending migrations (49a)

Catalog kind 9 is a chunked UTF-8, source-generated JSON `SqlCatalogPendingMigration` envelope.
It carries the complete target document/hash, ordered immutable step identities and their
one-based positions after inline steps. It uses the same strict chunk assembly and one storage
transaction as applied-schema records (kind 6). The SQL engine gates this addition with data
format 9; no legacy reader or upgrade path is added. Persisting intent does not advance DDL
generation or replace the old applied marker. Saving the completed applied marker deletes every
pending chunk in the same transaction. Open validates the envelope and ordering, and the engine
gates the target document's format before checking its hash or resuming. Catalog persistence knows no SQL executor,
worker, or hosting lifecycle.

The compiled document is now `cohesion/database-schema/v7` (#1108), matching Sdk.Database.
Applied-schema records (kind 6) and pending targets (kind 9) both retain the complete versioned
document, so their field layout does not change. The SQL record-space gate is now 20 (#1121).
SQL checks those document formats before opening
the data file set and before Apply/Verify compares persisted hashes. A v4 NUMERIC(p) document
with scale:null is refused through the schema format gate, with the rebuild/recreate remedy;
its old hash is never validated or reconciled. No compatibility reader or upgrade is added.

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

Table metadata extension version 2 introduced one collation identifier per column
after the existing constraints. Current format 26 requires the complete table extension 6 record,
including resolved-call vectors, identity facets and frozen missing-value literals described below. Column additions,
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
  ordinals' count and values from version 3. Earlier table records without the current
  format's required fields are refused; the engine preserves the recorded-version
  error through its format gate. A persisted layout that cannot describe the table (a negative
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
  which this catalog always writes. Current format 26 also requires the call vectors, identity facets and extension 6 missing-value literals;
  the format gate refuses earlier databases rather than loading their definitions. A persisted blank
  name fails the load with `SqlCatalogException`, as does a blank `domainName` at the
  column's constructor (`ArgumentException`).

## Why-this-not-that decisions

SQL namespaces (#1030) are immutable SqlCatalogNamespace descriptions, separate from the
compiled SqlCatalogSchemaState. Kind 15 stores strict source-generated JSON with Name, Owner
and OwningSchema. Namespaces join the atomic SqlCatalogSnapshot, so empty namespaces persist
and appear in SCHEMATA without inferring them from tables. CreateSchemaAsync and DropSchemaAsync
are instance operations, with declared-script publication overloads and an S: identity. Opening
the catalog seeds nothing and commits nothing; SQL creation completion seeds dbo before its final format marker.

DROP SCHEMA CASCADE serializes its complete mutation under the publication lock in one storage
transaction: namespace/table/index removal, incoming external-FK replacement, identity-counter
deletion, one kind-14 reclamation intent per table, registration replacement, one generation
advance and an optional receipt. Cache changes follow commit. SQL's
SqlPlanExecutor.Schemas.ObjectsInNamespace snapshot enumeration defines RESTRICT, ordered locks,
ownership and the CASCADE drop set; views (#1124) participate in that enumeration and its
revalidation. The catalog's commit-time contents check remains an independent invariant guard.
SQL owns metadata admission, ordered object locks and ownership checks; bare catalog table
operations continue to accept namespace strings without requiring a namespace record.

Format 21 fences the new namespace record. Earlier databases without first-class namespaces
are refused, rather than synthesized or upgraded on open: export with the old engine, recreate,
explicitly create non-dbo namespaces and import. Table extension version 6 is unchanged.

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
  each tree, behind this marker.) The engine writes its version (15) when it
  creates a database and refuses to open one whose marker reads anything else; it
  has no upgrade path (owner decisions of 2026-10-01 and 2026-10-02; upgrades are
  #1152). Versions 5 through 15 are stored as a kind-8 record like version 4. Absent marker
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

The ownership fields are an appended tuple suffix. Ad-hoc creation writes `Adhoc`
explicitly; ownership is never inferred from a database-wide schema marker. Invalid owner/name combinations are
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

## Renames and frozen missing values (#1122)

SqlCatalogColumn.DefaultLiteral remains the current insert-time default. MissingValueLiteral
is the independent frozen canonical literal captured at ADD COLUMN; absent means NULL.
Table extension 6 appends one optional missing literal per live column after the identity
facets; SQL record-space format 17 gates it. No record kind, upgrade path or compatibility
reader is added. Replacing column definitions preserves this facet, as it preserves ordinals.

RenameTableAsync and RenameColumnAsync retain the table ObjectId and physical layout. Under
the metadata lock they pre-encode all affected table/index records, rewrite incoming and self
FK target names and local/key column lists, then commit those records, generation and an optional
script receipt in one storage transaction. Cache publication follows commit. Existing index
registrations and identity counters are keyed by ObjectId and remain valid. Constraint/index
names remain unchanged. The engine supplies canonical CHECK text and proves its binding before
publication; Catalog requires a complete CHECK map but remains SQL-agnostic. A stale expected
table or any collision fails before publication. The engine takes the target Exclusive lock and
then child locks in ascending ObjectId order, rechecking identity and ownership after waits.

## Constraint persistence

Table records append a versioned extension after ownership: its current version is
`6` (#1122), followed by constraint count and each immutable foreign-key/check definition.
Later fields retain collations, physical layout, domains, call vectors and identity facets. A reference
stores its ordered local and target columns, target SQL namespace/table, and
one of five delete actions in its action byte: `RESTRICT` (0), `CASCADE` (1),
`NO ACTION` (2), `SET NULL` (3), or `SET DEFAULT` (4). A check stores its predicate as canonical
SQL text and resolved function calls (below). Records ending after the original primary keys
or ownership suffix lack format 10's required fields and are refused. Unknown extension versions and malformed
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

SQL record-space format 20 adds persisted NoAction, SetNull and SetDefault values beside
Restrict and Cascade in the existing action byte (#1121). Earlier formats' action guard refuses
these values, so the engine refuses format-19 and older catalogs before opening their data files;
there is no compatibility reader. Table extension 6 is unchanged: expression defaults and
their resolved call vectors use the existing canonical text fields; stable ADD COLUMN
results are independent frozen literals in MissingValueLiteral.

Record-space format 16 gates canonical identifier quoting for SEARCH and MATERIALIZED
(#1116). The engine renderer now quotes those identifiers in CHECK, DEFAULT and SQL
function text; format-15 CHECK text would report provisioning drift on ordinal canonical
comparison. Catalog tuple layouts are unchanged; the SQL engine owns the open-time gate.
Format 19 likewise gates canonical quoting for PERCENT and TIES (#1107),
without changing catalog tuple layouts or adding an upgrade reader.

Every SQL expression this catalog stores — table CHECKs, column literal and expression
DEFAULTs, domain CHECKs and literal DEFAULTs, and SQL function bodies — is
**canonical SQL that the engine rendered from the parsed
tree** (`SqlExpressionRenderer`, Sql.Language), never the text a user wrote. A check
declared as `QTY>0   and qty<100` is stored as `QTY > 0 AND qty < 100`; a default
declared as `'it''s'` or `+5` is stored as `'it''s'` or `5`, the SQL literal rather
than its bare value. New persisted expressions must follow the same rule; storing
source text is not an option, because a stored definition must mean the same thing
under every later parser. Expression defaults use this same path; persisted view queries do too.

Each expression also stores an ordered preorder vector of resolved function identities:
normalized name and parameter-type signature, including pseudo-types and variadic shape.
Repeated calls retain repeated entries; special forms have none. The column and constraint
vectors are required trailing table fields in format 10; SQL function/domain records carry
their body/check/default vectors. Literal-only defaults have empty vectors. These are format-10
record changes, with no second format bump or compatibility fallback.
Table tuples encode consecutive equal identities as runs and reconstruct the exact ordered
vector on load. This keeps a deeply nested CHECK within the one-page bound without repeating
the same signature text for every call. Invalid run lengths and excessive expanded counts are
refused before allocation.

The catalog itself stays SQL-agnostic: it has no reference to the language package,
stores the text and identities as opaque strings, and validates
only shape (a check needs non-blank text; a reference needs its columns). The SQL
engine owns the rule end to end. It renders and verifies the canonical text before
publishing a definition, and when it opens a database it parses and binds every
stored table check and default once per table version, and reconstructs SQL functions
and domain definitions on open. Calls bind only to the recorded identities, so added overloads
cannot replace existing CHECKs, DEFAULTs or bodies. DROP reads those same vectors. Missing
identities, and recorded native signatures whose volatility is now incompatible with their
persisted use, use the engine's unresolved-definition path: open and reads succeed, but use and
declared Build fail with `COHSQLE009`. CHECK rechecks Immutable admission at every bind;
other tightened declaration-only rules are not reapplied. A structurally malformed definition
fails open, naming the table and constraint or column. Canonical storage began in data-storage
format 4, so it needed no further
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
virtual/system flag. System-relation names, columns, binding and row projection belong to the SQL
engine. Persisted user views are immutable SqlCatalogView records in this catalog;
the engine still owns parsing, binding and execution.

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

## Identity counter records (#1123)

Catalog kind **16** is pre-assigned to identity counters (15 is reserved for #1030; 17 for
#1124). Its tuple is kind Int32, owning table ObjectId Int64, and last reserved value Int64.
There is at most one counter per table; load refuses duplicates, orphaned counters, out-of-range
values and values outside the START/INCREMENT progression. Generator bounds follow PostgreSQL
`src/backend/commands/sequence.c:1462,1494`: ascending 1 through the type maximum,
descending the type minimum through -1. Load builds one ObjectId-to-table map, so validating
all counters costs O(tables + counters), including the single pass over table columns.
Identity metadata is immutable SqlCatalogColumn.Identity / SqlCatalogIdentity in table-extension
version **5**. SQL record-space
format **15** supersedes 14; earlier files are refused, with no migration or compatibility reader.

AllocateIdentity is a concrete public catalog member, as ReserveTableAsync and PublishTableAsync
are. Under the existing metadata lock it durably reserves up to 32 values with checked Int128
intermediate arithmetic before delivering any. Reservations self-commit only the counter and
never replace the table object, advance DDL generation or publish script receipts. The cursor
and unused block stay private and volatile; reopen begins after the last reserved value, so
crashes cannot reuse a delivered value. Unconfirmed commits fence the catalog through its existing
storage-offline gate. Data transaction rollback does not reclaim values. DROP TABLE deletes the
counter in the catalog's unpublication transaction; dropping the identity column likewise retires
it atomically. A recreated table gets a fresh ObjectId and starts from START.

## Non-goals

- General sequences and permissions (permissions are
  `Sql.Security`'s feature, #177). Virtual system views are engine projections
  of this catalog and require no view records here.
- Multi-statement DDL atomicity (see self-committing DDL above). The migration
  layer compensates completed reversible statements on failure; an MVCC bracket
  spanning catalog and data DDL requires a future catalog batch-transaction seam.

## AOT posture

Value/data classes, the shared codec and source-generated System.Text.Json metadata; no reflection-based serialization.

## E4 definitions and script receipts

Phase E4 introduced format 10, previously 9 (superseded by format 11 for reclamation above).
Kinds 10/11 store immutable SqlCatalogFunction and
SqlCatalogDomain records: signatures/types, canonical SQL with resolved-call vectors,
volatility/null behavior, domain dependencies and Adhoc/Schema ownership. Publication/removal advances DDL generation. Snapshots
include both kinds. The engine owns semantic binding and dependency/ownership admission.
Native function ownership is resolved by the requested normalized signature; an unrelated
engine overload does not turn a missing SQL definition into an engine-owned one.
The engine retains parameter domain identities in both declaration and body binding, including
reopen, so domain-specific casts are selected inside function bodies. Return-domain checks cache
their supported argument widening and use the same converted invocation and NULL rules as column
and argument checks. Weakened native volatility propagates unresolved bindings through SQL wrappers
and domain CHECKs (including function signature and CAST dependencies); restoring the registration
on reopen restores writes.
The engine evaluates domain argument coercion before STRICT dispatch and caches SQL body
variants by invocation collation; the catalog persists the declarations and identities, not
bound runtime trees. The format gate refuses older files without an upgrade path.

Kind 12 stores SqlCatalogScript: case-sensitive name, exact-body SHA-256, statement count and
committed prefix, initial/latest DDL generations, typed schema hash and created table/index
identities. Each completed statement also records its catalog-derived target (empty for non-DDL)
and canonical SQL function parameter type spellings, paired with the function's normalized storage signature.
Domain names in parameter and result types are quoted, distinguishing a domain named `VARCHAR(1)`
from the built-in `VARCHAR(1)` type even after reopen; column domain names remain identifier values.
CREATE FUNCTION takes those spellings from the published definition; DROP FUNCTION receives the
SQL binder's already-resolved requested spellings as explicit mutation input. SQL owns type-name
resolution, including native domains; the catalog owns the actual removed function identity and
validates the parameter count. These records let later Builds validate completed function targets
after a domain or its declaration has been removed or redefined. Kind 13 stores the ordered set's
digest and reconciled generation. These changes retain the unreleased format 10. Tuple payloads use
source-generated System.Text.Json metadata with unknown-member refusal; no reflection serializer.
Definitions and receipts must fit the existing one-page record bound. The declaration's body stays
in application code, not the receipt.

Object identities use the catalog's stateless `SqlObjectIdentity` encoder, compiled from shared
source into Catalog and SQL. Each identifier component is SQL-delimited with double quotes and
every embedded double quote is doubled: `T:"a"."b.c"` and `T:"a.b"."c"` identify different tables,
as do `I:"a"."b"."c.d"` and `I:"a"."b.c"."d"` for indexes. Domain targets use `D:"name"`; function
targets use `F:` followed by a signature with a quoted normalized name and separately encoded
parameter types, including quoted pseudo-type names. The same encoding identifies publication
expectations, persisted statement targets, created-object ownership claims, later-Build receipt
validation and the provisioner's script-object checks. Name comparisons use the catalog's
`OrdinalIgnoreCase` rule. Script names, checksums and exact preceding receipt-state vectors retain
ordinal comparisons. The encoding change stays within unreleased catalog format 10, without a
second bump or a legacy reader.

Ordinary mutation methods retain their pre-E4 signatures. Explicit publication overloads take
`SqlCatalogScriptPublication` before a final optional CancellationToken, with one parameter per
line and full XML documentation. This immutable expectation names the statement's intended
table, index, function signature or domain and supplies the exact preceding receipt and live
DDL generation. Under the metadata lock, the catalog compares all preceding receipt fields and
vectors, generation and target with durable state and its actual mutation before committing;
a mismatch is `COHSQLS001`. The catalog derives every object claim from that mutation and commits
the new receipt with its metadata. Callers cannot publish a progress DTO or supply object claims.
This is an API consistency contract, not a boundary against callers controlling catalog storage.

`BeginScriptAsync` records the script identity before effects. `ReserveTableAsync` retains the
prior prefix while reserving an identifier; publishing `CreateTableAsync` completes the statement.
An IF EXISTS drop that finds its target absent records that verified absence and completes the
prefix without advancing DDL generation. `CompleteScriptStatementAsync` accepts only a non-DDL
expectation after the read or data statement succeeds; the engine owns atomic DML receipts.
`SaveScriptStateAsync` checks generation before saving the O(1) marker. Except for that skip,
Apply and Verify validate each completed statement's recorded target against the declared SQL,
including its retained function type spellings, and validate created table/index ownership;
inconsistency is `COHSQLS001`. No multi-statement catalog transaction was introduced. History is
forward-only.

The engine preflights declared scripts against the typed schema before opening files: scripts
cannot ALTER/DROP typed tables or DROP typed indexes (`COHSQLS005`). Script-created tables may
evolve through later scripts, and script-created indexes on typed tables remain separately
tracked. The typed schema document therefore stays an accurate record of its own objects when
the engine advances its generation after a script commit.

## Persisted user views (#1124)

Kind **17**, pre-assigned to views, stores strict source-generated JSON through
an internal DTO: namespace/name, canonical body, frozen output names and types,
domains/collations, stored-table ObjectIds, column/view/function/domain dependencies,
ordered resolved calls and ownership. SqlCatalogView and snapshot Views expose
immutable descriptions; tables retain their own stored-only shape. View columns are always nullable
in catalog metadata, following PostgreSQL's unconstrained view attributes
(`src/backend/catalog/information_schema.sql:674-676`). The new format-23 record stores no nullability
flag, and COLUMNS never needs to bind a view body. No interface, friend grant or language reference
was added. Format **23** fences kind 17; table extension 6 is unchanged. Older
formats are refused, with no upgrade reader. The encoded record must fit one page
(**8,092 bytes**); oversized definitions fail CREATE with COHSQLE033.
View refusals carry structured diagnostics on SqlCatalogException, including COHSQLE033 for
incompatible replacement and oversized records, so the wire preserves the same code.

CreateViewAsync validates collisions, live dependency identities, cycles and the
append-only replacement contract under the metadata lock, then commits record,
generation and optional V: script receipt together. Replacement preserves column
names, types, modifiers, domains and collations, following PostgreSQL
`src/backend/commands/view.c:266-325`. Every CREATE VIEW, including OR REPLACE,
records its target claim when run by a declared script. DropViewAsync commits
RESTRICT or dependent-view CASCADE and optional absence receipts atomically.
Table create/reserve/publish/rename share the view relation namespace. Table/column
drops, textual renames and changes to type/length/precision/scale independently enforce
view dependencies at publication (COHSQLD002; PostgreSQL `src/backend/commands/tablecmds.c:15612-15624`).
DROP SCHEMA includes contained and external dependent views in its one atomic
transaction. Engine ownership and DDL admission precede these invariant guards.

View body caches retain immutable catalog images by source-definition identity.
Equivalent captures share bindings; a snapshot transaction's COLUMNS projection
binds against its retained tables, views and SQL definitions after later drops.
Historical image entries are weak and stay alive with their retained captures;
ordinary view reads use a DDL-generation fast path. No snapshot generation API or
friend grant was added for this purpose.

## Interrupted creation (#1158)

The internal stateless SqlCatalogCreation predicate owns the creation-only record kinds and
initial counter constraints. SQL compiles it through the existing catalog shared-source link,
so recovery decisions use catalog vocabulary without a public API or friend-assembly grant.
Creation commits collation, dbo and the format marker in that order. Completion changes no
catalog record layout. Record-space format 26 additionally requires the SQL engine's durable
sql.create claim when adopting an interrupted file set; an empty data scan alone is no proof.
