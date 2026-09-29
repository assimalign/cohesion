# Blob catalog

`Assimalign.Cohesion.Database.Blob.Catalog` persists the containers and blob metadata of
one logical database. Its public factory returns `IBlobCatalog`; the implementation
is internal. It depends on the Database root for the ownership vocabulary, Blob.Storage
for record access, and the shared Storage and Transactions kernels for durability and MVCC.

Open `BlobCatalog` after the database coordinator has scrubbed recovered records. Container
records carry a stable identity, case-sensitive name, `DatabaseObjectOwner`, and optional owning
schema. Blob records carry the container identity, name, length, media type, entity tag,
creation/modification times, content checksum, and chunk-chain head.

Supply an `ITransactionContext` to every mutation and a `TransactionSnapshot` to every read.
The caller acquires the model locks and owns commit or rollback. The catalog never commits
the supplied transaction. Its metadata shares the content storage and journal so publication
of a new head and its chunks has one logical commit decision.

Blob listing supports an ordinal prefix and reads metadata records only. Ownership enforcement
belongs to the Blob engine; the catalog can persist a directly marked schema-owned container
without introducing compiled-schema provisioning.

See [DESIGN.md](DESIGN.md) for the directory, format, and recovery rules.
