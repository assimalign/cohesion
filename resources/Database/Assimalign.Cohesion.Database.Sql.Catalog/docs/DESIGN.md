# Assimalign.Cohesion.Database.Sql.Catalog — Design

The SQL model's schema authority (area architecture:
[resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.3). The catalog answers exactly
two questions for the planner: *what objects exist* (with stable identities) and
*what shape are they* — and it must answer identically after any crash.

## Collation metadata (#1025)

`SqlCatalogColumn.Collation` is an optional string-column override. Null inherits
`ISqlCatalog.DefaultCollation`, which is Binary when no default record exists.
The default is persisted in a dedicated kind-7 catalog record and captured in
immutable statement snapshots.

`DefaultCollation` is read-only on the contract. The default is established when
the catalog is opened — `SqlCatalog.Open(storage, defaultCollation)` — and is
fixed for the lifetime of the database, because every index key on a column that
inherited it is encoded through that collation's byte transform. Opening an
already-populated catalog under a different default is rejected; reopening under
the same one, or with no default supplied, keeps the persisted value.

Making this creation-time state rather than a mutator is deliberate: a setter on
the contract would tell every implementer the value is changeable and then guard
that promise at runtime, which is the kind of one-off bridging API the repo's
abstraction rule exists to keep off interfaces.

Table metadata extension version 2 appends one collation identifier per column
after the existing constraints. Version-1 and pre-extension records still read
with null column overrides and the Binary database fallback. Column additions,
drops, and restart preserve explicit overrides. An index inherits its key
columns' effective collations; the SQL engine verifies index eligibility and
uses those same transforms for uniqueness enforcement.

## Why-this-not-that decisions

- **A dedicated catalog storage file set** — not catalog rows mixed into the data
  file. The current row facade exposes one record space per storage instance;
  sharing it would put metadata records inside user table scans. A second
  `SqlStorage` (by convention `<database>.catalog`) buys complete isolation while
  reusing the exact same durability machinery — no bespoke metadata persistence.
  When the data file gains per-object record spaces, folding the catalog back in
  becomes an option, not a requirement.
- **Self-committing DDL.** Every catalog mutation runs in its own storage
  transaction and is durable (WAL commit record) when the call returns. DDL inside
  a user DML transaction is deliberately unsupported in the MVP: the in-memory
  cache updates on commit, and half-visible schema changes are a correctness trap
  (historically mishandled by real engines). The engine serializes DDL per
  database.
- **The shared tuple codec as the record format.** Catalog records are encoded
  with `DatabaseKeyWriter`/`DatabaseKeyReader` (#854): self-describing, exact
  round-trips, one codec maintained in one place. Ordering (the codec's other
  property) is irrelevant here — reuse beats a second bespoke format.
- **One record per table.** Columns and the primary key fold into the table's
  record: schema changes rewrite one record (in place when it fits, relocating —
  delete + insert — when it grows). Per-column records would buy nothing at this
  scale and cost multi-record consistency.
- **Object identities are catalog-assigned `ulong`s** persisted with a counter
  record, monotonic across reopen (the loader also raises the counter past every
  loaded table, so a torn counter update can never recycle an id). Data rows,
  index registrations, and lock resources key off these ids.
- **Index directory persistence lives here** — the index manager stays a physical
  component (`Database.Indexing`'s documented split): the catalog stores the
  exported `BTreeIndexRegistration` set and hands it back for re-attachment on
  open. Root page ids drift on splits; the engine re-saves at its persistence
  points (checkpoint/shutdown).
- **Index descriptions are schema metadata, one record per index (kind 5)** —
  `SqlCatalogIndex`: name (unique per table, case-insensitive), owning table
  object id, ordered key columns, uniqueness. The description is deliberately
  separate from the physical registration: the description is stable while root
  page ids drift, and the planner needs columns/uniqueness the registration
  doesn't carry. **Description and registration writes are atomic** —
  `CreateIndexAsync`/`DropIndexAsync` take the registration set and persist both
  records in one self-committing transaction, because a crash must never leave a
  description promising an index no tree backs (an unenforced UNIQUE) or a
  registration re-attaching a tree no statement can reach. `DropTableAsync`
  removes the table's descriptions and registrations the same way. Guards:
  dropping an indexed column is rejected (entries key on its values); index
  columns must exist at creation.
- **The record-space format version lives here** (kind-4 record,
  `RecordSpaceFormatVersion`): data rows are not self-describing across layout
  changes — a stamped (MVCC, version ≥ 2) record and an unstamped (version 1)
  record cannot be told apart record-by-record, and version 2 vs 3 (shared page
  stream vs per-object page chains) is a page-placement property no record
  carries — so the database-grain marker is catalog metadata, read by the
  engine at open to decide which in-place upgrade stages to run. Absent marker
  reads as version 1 (pre-marker databases); the engine writes the current
  version (3) after upgrading (or at creation, when the space is born on the
  current format).
- **The applied compiled-schema state lives here** (kind-6 records). The catalog
  stores the lowercase content hash together with the complete canonical schema
  document. Documents are strict UTF-8 and chunked into bounded records; replacing
  all old chunks with all new chunks is one self-committing storage transaction,
  so reopen observes either complete state. The SQL provisioner verifies both the
  document/hash pair and the live table/index catalog before treating a repeated
  apply as a no-op; the marker is never an authority over detected drift.

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
change the on-disk format, and catalogs written by the contract-freeze build load
without a compatibility alias. The schema hash/document record remains unchanged
and continues to support drift detection independently of per-object ownership.

The engine enforces the live-session DDL lock; the catalog remains the durable
metadata component used by sanctioned schema application as well. Ownership
metadata is accepted by the staged-publication statics on `SqlCatalog`;
authorization to change an existing schema-owned object remains an engine/session
decision. `ISqlCatalog` itself has no new member. The older direct-creation helpers
remain internal and are used only by catalog tests.

## Constraint persistence

Table records now append a versioned constraint extension after ownership: version
`1`, constraint count, and each immutable foreign-key/check definition. A reference
stores its ordered local and target columns, target SQL namespace/table, and
`RESTRICT` or `CASCADE` delete action. A check stores its SQL expression. Records
ending after the original primary keys or ownership suffix still load with no
constraints. Unknown extension versions and malformed definitions fail closed.
Column changes retain constraints, and add/drop constraint rewrites use the same
WAL-backed, self-committing record replacement as existing catalog metadata.

`UNIQUE` uses `SqlCatalogIndex.IsUnique`, matching `CompiledSchemaIndex.IsUnique`;
it does not acquire a competing foreign-key/check constraint kind. Creation of a
table with unique declarations reserves and durably advances its object identity,
then the engine commits the empty index trees before publishing the table,
constraints, index descriptions, and registrations in one catalog transaction.
A crash before publication leaves no visible table with missing enforcement.
Replacement publication similarly commits newly added columns/constraints and
their new indexes together. `SqlCatalog.ReserveTableAsync(ISqlCatalog, ...)` and
`SqlCatalog.PublishTableAsync(ISqlCatalog, ...)` expose this composition lifecycle as
`public static` methods that downcast to the internal implementation - the same bridge
shape as the existing `CreateTableAsync`/`AddConstraintAsync` helpers, and for the same
reason: the lifecycle is a capability of *this* catalog, not a contract every
`ISqlCatalog` implementation must honour. A reservation
persists only the identity counter and does not lock the name; callers serialize
DDL and durably build enforcing indexes before publishing. New publications reject
zero or unallocated identities so they cannot bypass the durable identity counter.
Replacement publication
retains existing index descriptions while adding the supplied new descriptions.
`SqlCatalog.DropConstraintAsync(ISqlCatalog, ...)` owns removal of persisted
foreign-key/check metadata, alongside the existing table, column, and index mutations
on the interface.

Index records have their own version-`1` trailing extension carrying `IsPrimaryKey`.
This identifies the physical index enforcing primary-key metadata, allowing schema
reconciliation to distinguish it from a separately declared unique index on the
same columns. Older index records have no marker and load as ordinary indexes.

## Single source for SQL system views (C1)

The engine's `INFORMATION_SCHEMA` and `COHESION_SCHEMA` relations project this
catalog's table, column, key, index, constraint, and ownership descriptions.
Those descriptions remain the single source of truth: system views are computed
when queried and never inserted as stored catalog tables or copied into a second
metadata store. `SqlCatalogTable` describes stored objects only and gains no
virtual/system flag. SQL view names, columns, binding, and row projection belong
to the SQL engine, not to this persistence library.

`SqlCatalog.CaptureSnapshot(ISqlCatalog)` returns an `ISqlCatalogSnapshot` containing
an atomic capture of tables, index descriptions, and default collation under the
catalog's metadata lock. `ISqlCatalogSnapshot` is public because it is the return type
of a public static method; the implementation stays internal; callers can retain
the read-only capture without holding a storage handle or disposing it. Table
columns, primary-key columns, and index key-column names are copied into read-only
collections when descriptions are created; callers cannot mutate retained input
lists to alter a published table or an already captured directory. The SQL
session captures it at transaction begin for snapshot isolation and at statement
start for `ReadCommitted` or auto-commit. The engine derives every view row and
referenced constraint from that capture, preventing an enumeration from mixing
metadata before and after a DDL publication. Captures expose existing catalog
descriptions, with no SQL view binding, query execution, or mutable persistence
capability. These `SqlCatalog` statics replace the shipped-to-shipped friend grant
without widening `ISqlCatalog`: consistent reads and staged publication are
capabilities of this catalog implementation, and putting them on the interface would
make every future implementation owe four more members.
After a table drop, fresh snapshots contain none of its table, index, constraint,
column, or ownership metadata. See the SQL engine's
[virtual relation design](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#virtual-system-relations-c1)
for the query surface and the two deliberately non-standard extension views.

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

Value/data classes plus the shared codec — no reflection, no serialization
libraries.
