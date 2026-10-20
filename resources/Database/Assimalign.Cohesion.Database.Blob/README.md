# Assimalign.Cohesion.Database.Blob

The Blob engine provides named databases, containers, streaming objects and a wire server.
Catalog and Storage are components of this assembly; wire contracts live in `Blob.Protocol`.

## Catalog component

`src/Catalog/` owns versioned container and blob metadata, ownership and transactional publication.

## Storage component

`src/Storage/` binds chunked blob streams to the shared page, journal and transaction kernel.

- [Overview](docs/OVERVIEW.md)
- [Design and on-disk format](docs/DESIGN.md)
