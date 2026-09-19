# SqlMapperGenerator

`SqlMapperGenerator` is the public Roslyn `IIncrementalGenerator` entry point. Its parameterless
constructor lets the compiler instantiate it; `Initialize` registers semantic schema discovery
and generated source output. The compiler owns its lifecycle.

It emits an entity-namespace `<Entity>Mapper` with a parameterless constructor and these members:

| Member | Purpose |
| --- | --- |
| `GetKey(entity)` | Extract declared identity without discovery |
| `Capture(entity)` | Own a detached snapshot of declared persistent values |
| `AreEqual(left, right)` | Detect changes according to the documented value semantics |
| `Read(source)` | Materialize an entity from schema-ordered values |
| `Write(entity, target)` | Fill schema-ordered values from direct entity member access |
| Nested `Snapshot` | Carry captured state into transactional writes |
| `Snapshot.<Member>` | Expose each captured declared member to adapters; binary getters copy |

With `Database.Sql.Mapping` referenced, the mapper implements `ISqlEntityMapping` and additionally emits:

| Member | Purpose |
| --- | --- |
| `TableName` | Bind SQL operations to the retained table name |
| `ColumnNames` | Preserve the retained projection and value order |
| `ColumnTypes` | Validate query operands against retained SQL storage types before emission |
| `KeyColumnName` | Bind updates/deletes to the declared application-assigned identity |
| `ReferencedTables` | Supply foreign-key dependency order to the transactional adapter |
| `WriteSnapshot(snapshot, target)` | Stage detached captured values with exact SQL storage conversions |
| Static `SchemaTable` | Deploy immutable table metadata from the same declaration without runtime discovery |
| Nested static `Columns` | Group statically typed query columns |
| `Columns.<Member>` | Supply `SqlColumn<TEntity,TValue>` for each declared member's predicates and ordering |

Public entities receive public mappers; other accessible entities receive internal mappers. The
snapshot constructor is internal and reachable only through mapper capture from public consumers.
All generated public members include XML documentation. See [the design](../../../DESIGN.md) for
diagnostics, scalar representations and null/ownership semantics.
