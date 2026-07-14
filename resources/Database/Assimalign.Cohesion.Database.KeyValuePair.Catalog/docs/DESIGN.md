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
  space's page chain); the name scopes the marker to the model's own vocabulary.
  There is no upgrade machinery because there is no older format — the engine
  writes the marker at creation and rejects unknown newer versions at open.

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
