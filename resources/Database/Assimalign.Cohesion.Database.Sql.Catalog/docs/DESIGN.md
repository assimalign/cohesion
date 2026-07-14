# Assimalign.Cohesion.Database.Sql.Catalog — Design

The SQL model's schema authority (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md) §3.3). The catalog answers exactly
two questions for the planner: *what objects exist* (with stable identities) and
*what shape are they* — and it must answer identically after any crash.

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

## Error model

`SqlCatalogException : DatabaseException` for catalog violations (duplicate or
missing tables/columns, primary-key drops, malformed persisted records).

## Non-goals

- Foreign keys and check constraints (the dialect doesn't parse them yet; the
  record format has room).
- Views, sequences, permissions (permissions are `Sql.Security`'s feature, #177).
- Multi-statement DDL atomicity (see self-committing DDL above).

## AOT posture

Value/data classes plus the shared codec — no reflection, no serialization
libraries.
