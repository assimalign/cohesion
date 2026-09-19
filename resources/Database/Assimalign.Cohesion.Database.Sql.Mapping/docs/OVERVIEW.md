# Database.Sql.Mapping

The SQL adapter maps retained `SqlSchema` declarations onto the SQL client and the
shared mapping unit of work. The existing `SqlMapperGenerator` emits static entity
readers, detached snapshots, primary-key access, column tokens and foreign-key
dependencies from the same declarations used to deploy the database. Enable it with
`CohesionGenerateDatabaseMappers=true` in the consuming project.

NuGet consumers receive the generator through the `Database.Mapping` dependency.
The SQL adapter allows that analyzer asset to flow transitively instead of bundling
a second copy, so referencing both mapping packages still loads one generator.
Plain SDK consumers also expose the opt-in property to the analyzer:

```xml
<PropertyGroup>
  <CohesionGenerateDatabaseMappers>true</CohesionGenerateDatabaseMappers>
</PropertyGroup>
<ItemGroup>
  <CompilerVisibleProperty Include="CohesionGenerateDatabaseMappers" />
</ItemGroup>
```

The adapter references `Database.Mapping`, `Database.Sql.Client` and `Database.Types`; it owns no
engine, schema deployment service, connection pool or application host. The caller
owns the SQL client and passes it to `SqlMappingStore`.

```csharp
var store = new SqlMappingStore(client);
var work = MappingUnitOfWork.Create<SqlMappingTransaction>(store);
var customers = SqlMapping.Register(work, new CustomerMapper());
var orders = SqlMapping.Register(work, new OrderMapper());

customers.Add(new Customer { Id = 1, Name = "Ada" });
orders.Add(new Order { Id = 42, CustomerId = 1 });
await work.SaveChangesAsync();

var query = SqlMapping.Query(new OrderMapper())
    .Where(OrderMapper.Columns.CustomerId.Equal(1))
    .OrderBy(OrderMapper.Columns.Id)
    .Take(20);
var results = await store.QueryAsync(query);
foreach (var order in results) orders.Attach(order);
```

Each save buffers generated parameterized inserts, updates and deletes, orders
them by the retained foreign-key dependencies, and executes them through one
explicit SQL transaction. Queries materialize detached entities; attaching them
explicitly preserves the core's canonical instance and local changes. Relationship
loading uses typed foreign-key predicates. There is no automatic navigation fixup.

The immutable query builder offers equality and ordered scalar comparisons, null
tests, Boolean composition, ordering, `DISTINCT`, `LIMIT` and `OFFSET`. Every value
is a bound parameter and every identifier comes from the retained mapping. The
builder has no raw expression, DDL, join, aggregate or set-operation entry point.
Its scope is a measured subset of the engine's
[declared dialect](../../Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md).
Binary and floating-point values support materialization, ordering, distinctness
and null tests. Their non-null comparison predicates fail precisely before I/O
because the current evaluator does not support their complete value domains.

Code-first ownership concerns schema structure: ordinary entity DML against a
`DatabaseObjectOwner.Schema` table is supported, while the mapper has no ability to
create, alter, drop or reassign ownership of tables or indexes. Schema deployment
remains the sole owner of changes to schema-owned objects. The engine separately
allows independently owned ad-hoc indexes on those tables; this mapper exposes no
index-management operations.

A successful save has an acknowledged commit. A failure before COMMIT rolls back.
A lost COMMIT acknowledgement has an **unknown outcome**: the adapter throws
`MappingCommitOutcomeUnknownException`, permanently faults its store and unit of
work, and discards the connection. It never retries that save. See the
[design and reconciliation boundary](DESIGN.md#commit-outcome-and-reconciliation)
before deciding when an application may create a fresh scope.

Application-assigned immutable scalar keys and detached binary snapshots are
supported. SQL generation rejects binary, floating-point, DateTime and DateTimeOffset
keys because SQL equality cannot identify their encoded representations reliably;
those types remain available as ordinary columns. Generated keys, proxies, deep mutable-object tracking, implicit
navigation fixup, cyclic dependency scheduling and cross-store transactions are
outside this adapter. The NativeAOT guard in `samples/` exercises the actual SQL
engine, SQL wire client, generated mapper and shared save scope.
