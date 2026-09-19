# Blob storage

`Assimalign.Cohesion.Database.Blob.Storage` supplies chunk streams and the record
adapter used by the Blob engine and catalog. It references the shared
`Database.Storage` and `Database.Transactions` packages. Pages, page checksums,
free-space tracking, journal framing, physical recovery, locks, snapshots, and
logical recovery remain kernel responsibilities.

Create or open one `BlobStorage` file set, construct a `TransactionCoordinator`
with its `WriteAheadJournal` and `Records`, then perform the coordinator's normal
recovery sequence before accepting operations. Catalog records and chunks share
this file set and logical transaction. Metadata uses owner-zero pages, so catalog
listing does not scan blob content.

`OpenWrite` returns a forward-only stream holding one chunk buffer. Its completion
callback receives the chain head, length, and IEEE CRC-32 for atomic metadata
publication. `OpenRead` follows that reference one chunk at a time. An optional
disposal callback keeps the caller's snapshot pinned until the download closes.
Cancellation and failed uploads invoke the abort callback and cannot publish.

The [design](DESIGN.md) specifies the binary format and recovery invariants.
