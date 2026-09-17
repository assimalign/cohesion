# Graph catalog API

`GraphCatalog.Open` creates an `IGraphCatalog` over graph storage and its transaction
coordinator. `GraphLabelMetadata` and `GraphRelationshipTypeMetadata` carry stable GUIDs,
case-sensitive names, ownership, and owning schema. `GraphPropertyKeyMetadata` declares
optional value type and requiredness. `GraphIndexMetadata` names a node-property index.

Read methods accept a visibility snapshot. Save and delete methods join an active logical
transaction and require the caller to serialize definition changes. Saving a new marked
schema definition is supported; changing it later throws `DatabaseObjectLockedException`.
Malformed persisted records and conflicting definitions throw `GraphCatalogException`.

Physical index creation and maintenance belong to `Graph.Storage`; the engine coordinates
those operations with catalog metadata in one transaction. The catalog does not own storage
or coordinator disposal.
