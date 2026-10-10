# Assimalign.Cohesion.Database.KeyValuePair.Catalog — Overview

The key-value model's metadata catalog: the sealed `KeyValueCatalog`, opened over a
database's dedicated catalog storage file set (`KeyValueCatalog.Open`).

## Purpose

Persists exactly what re-attaches a key-value database on open:

- **Index registrations** — the physical identity (object id, definition, root
  page id) of the database's primary key index, exported by the index manager and
  re-persisted at the engine's persistence points (a backstop: the B+Tree keeps its
  root page fixed through splits since #1159).
- **Entry-space format version** — the data-storage layout marker (entry records
  and the primary index tree, 2 since #1194); neither is self-describing across
  format changes, so the engine reads this at open and refuses any format but its
  own.

## Scope

Deliberately minimal — the key-value model has no schemas, tables, columns, or
constraints beyond key uniqueness (enforced by the primary index itself, not
described here). Deferred model features with catalog surface area when they land:
named key spaces, per-entry expiration (TTL) metadata.

## Dependencies

- `Assimalign.Cohesion.Database` — the area root (`DatabaseException` ancestry).
- `Assimalign.Cohesion.Database.Storage` / `.KeyValuePair.Storage` — the catalog
  file set the records persist on.
- `Assimalign.Cohesion.Database.Types` — the shared tuple codec records encode with.
- `Assimalign.Cohesion.Database.Indexing` — `BTreeIndexRegistration`.

## Usage

The engine (`Assimalign.Cohesion.Database.KeyValuePair`) opens one catalog per
database over the `<name>.catalog` file set; consumers never compose it directly.
