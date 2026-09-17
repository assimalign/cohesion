# Documents.Catalog

`Assimalign.Cohesion.Database.Documents.Catalog` supplies `IDocumentCatalog`, implemented
internally over the same `DocumentStorage` file set as document content. It stores collection
ownership, versioned document identities/content references, and secondary index definitions.
Its B+Trees come from `Database.Indexing`, and every data/index mutation joins the caller's
`ITransactionContext` through `TransactionCoordinator`.

`DocumentCatalog.Open(storage, coordinator)` opens the metadata directory and persisted physical
trees. Read methods accept an explicit visibility snapshot. Mutation methods neither begin nor
commit the caller's transaction. The engine takes the logical database's exclusive writer lock,
rejects write conflicts and stale index definitions, and enforces schema ownership before calling
the catalog.

`CreateIndexAsync` builds a scalar-path index immediately. `SaveDocumentAsync` and
`DeleteDocumentAsync` maintain every visible index alongside metadata writes; queries do not
rebuild indexes. The Documents engine reaches index creation and deletion through planned OQL
`CREATE INDEX` and `DROP INDEX` statements; these catalog methods remain lower-level transactional
composition seams. `SearchIndexAsync` performs a B+Tree equality or range seek and resolves only
snapshot-visible document versions, ordered by ordinal identity. See [DESIGN.md](DESIGN.md) for
the format and the precise path/scalar semantics.
