# Assimalign.Cohesion.Database.Sql.Storage — Overview

The SQL model's storage facade: `SqlStorage` is the concrete `Storage` implementation
(model `StorageModel.Sql`) the SQL engine composes per database file set. It exposes
row-grain operations — insert/read/update/delete by `(PageId, SlotIndex)` — over the
shared page/WAL substrate from `Assimalign.Cohesion.Database.Storage`, plus the
factory methods (`Create`/`Open`) the engine's storage strategies call.

- **Consumed by:** `Assimalign.Cohesion.Database.Sql` (the engine, per database:
  one data file set + one `.catalog` file set) and `Assimalign.Cohesion.Database.Sql.Catalog`
  (metadata records on the catalog file set).
- **Depends on:** `Assimalign.Cohesion.Database.Storage` only.
- **Docs:** design record in [DESIGN.md](DESIGN.md); the substrate's invariants
  (WAL ordering, recovery, buffer pool, per-owner record chains) are documented in
  the Storage project's `docs/DESIGN.md`.
