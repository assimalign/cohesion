# Assimalign.Cohesion.Database.KeyValuePair.Storage — Overview

The key-value model's storage binding: `KeyValueStorage`, the model-typed storage
file set (`StorageModel.KeyValue`) the key-value engine composes over the shared
`Database.Storage` kernel.

## Purpose

`KeyValueStorage` derives from the kernel's `Storage` base and exposes exactly the
record surface the key-value engine needs — entry-record insert/read/update/delete
on the transactional page substrate, with owner-chain inserts keyed by the engine's
key-space object id (per-object page locality). No physical machinery lives here:
pages, buffer pool, free-space map, WAL, and recovery are the kernel's, unchanged.

## Scope

- `KeyValueStorage` — factories (`Create`/`Open`, with the `checkpointOnOpen: false`
  open the engine's recovery analysis requires) and the entry-record operations.
- Nothing else. Entry encoding (version stamps + key/value tuple) belongs to the
  engine (`Assimalign.Cohesion.Database.KeyValuePair`), mirroring the SQL family
  split where `SqlRowCodec` lives in `Database.Sql`, not `Sql.Storage`.

## Dependencies

- `Assimalign.Cohesion.Database.Storage` — the shared physical kernel (pages, WAL,
  recovery, transactional record surface).

## Usage

The engine's storage strategies (`Assimalign.Cohesion.Database.KeyValuePair`'s
in-memory and file-system strategies) create and open instances; consumers never
compose this type directly.
