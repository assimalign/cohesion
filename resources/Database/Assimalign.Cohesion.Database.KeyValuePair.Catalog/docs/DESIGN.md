# Assimalign.Cohesion.Database.KeyValuePair.Catalog — Design

## Design intent

The smallest catalog in the model family, on purpose. A key-value database's only
durable metadata is what re-attaches it on open: the primary index's physical
registration and the entry-space format version. Everything else a relational
catalog carries (schemas, tables, columns, constraint descriptions) has no
key-value counterpart — the parent issue's design note ("do not hide catalog and
security semantics behind a raw dictionary metaphor") is honored by making the
catalog **explicit and small**, not by inventing relational ceremony for a model
that has none.

## Why-this-not-that

- **A dedicated catalog file set, not records mixed into the data file.** The SQL
  family precedent (`<name>` + `<name>.catalog`) holds: catalog writes are
  self-committing storage transactions with no MVCC layer above them, and mixing
  self-committing records into a data file whose journal carries logical
  transaction lifecycles would entangle recovery classification with metadata
  writes. Symmetry also keeps the engine's storage strategy identical to SQL's.
- **The `DefaultSqlCatalog` persistence pattern, reduced.** One tuple-codec record
  per concern (kind 1 = registrations, kind 2 = format version), rewritten in
  place when it fits and relocated when it grows. The registration codec matches
  `DefaultSqlCatalog`'s byte-for-byte so the family stays mutually legible; a
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

## Query-time introspection captures (C2)

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

## One sealed type (concrete-types plan, phase 4, #1260)

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

## Error model

`KeyValueCatalogException : DatabaseException` — a malformed persisted record or
invalid metadata write. Catalog failures are database failures to callers.

## Non-goals

- Key-space registries (multiple named key spaces per database) — deferred; the
  engine currently owns one implicit key space (object id 1).
- TTL/expiration metadata — deferred with the model feature.
- Constraint descriptions — key uniqueness is enforced physically by the unique
  primary index; there is nothing to describe.

## AOT posture

Tuple-codec encode/decode only; no reflection, no serialization frameworks.
