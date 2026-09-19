# Database.Mapping

`Assimalign.Cohesion.Database.Mapping` supplies entity identity, explicit snapshot change tracking,
materialization contracts, and one atomic `SaveChangesAsync` across registered mappings. It is a
Database feature library targeting .NET 10 with NativeAOT compatibility. It references the Database
area root and has no hosting, SQL schema, query, or transport dependency.

Create a scope with `MappingUnitOfWork.Create(store)`, register each logical mapping once with its
mapper and transactional writer, and retain the returned `ITrackedEntities<TEntity, TKey>`.
Materialize with an `IEntityReader<TEntity, TSource>` and pass the result to `Attach`; always use
the returned canonical instance. Call `Add` for new entities and `Remove` for deletion. A save
compares detached snapshots and submits every change in one adapter transaction. A successful save
returns the number of changed entities; a no-op save opens no transaction.

Mappings supply application-assigned, immutable keys. Generated SQL mappings track only schema
members; changing an unrelated CLR property does not cause a write. Scalar replacement and in-place
changes to mapped byte arrays are detected. Navigation fixup, proxies, graph traversal, nested
document tracking and generated store keys are outside this foundation.

The existing `Assimalign.Cohesion.SourceGeneration.Database` analyzer generates readers, writers,
keys and snapshots from the existing `SqlSchema` C# declarations. Its emitted SQL value-list shape
lives in the consuming program; the core's materialization contracts preserve arbitrary source and
target types. The generator ships with the Mapping NuGet package and the App.Database reference
pack. `Sdk.Database` consumers enable it with `<CohesionGenerateDatabaseMappers>true</CohesionGenerateDatabaseMappers>`.
Plain SDK consumers referencing the Mapping and SQL schema packages also expose the property:

```xml
<PropertyGroup>
  <CohesionGenerateDatabaseMappers>true</CohesionGenerateDatabaseMappers>
</PropertyGroup>
<ItemGroup>
  <CompilerVisibleProperty Include="CohesionGenerateDatabaseMappers" />
</ItemGroup>
```

Generation is disabled by default. This switch adds no schema description; the same C# declaration
remains authoritative. See the [generator design](../../../../analyzers/Assimalign.Cohesion.SourceGeneration.Database/docs/DESIGN.md).

Adapters must implement atomic commit and rollback-on-disposal. If a commit response is lost,
`MappingCommitOutcomeUnknownException` permanently faults the unit of work and its tracked sets.
It does not imply rollback or permit replay. See [DESIGN.md](DESIGN.md) for the explicit boundary
and SQL reconciliation requirements.

Tests are in `tests/Assimalign.Cohesion.Database.Mapping.Tests.csproj`. The executable NativeAOT
guard lives in `samples/Assimalign.Cohesion.Database.Mapping.AotGuard/`; its README gives publish
and run commands. The guard executes the generated mapper and unit of work, without executing the
existing runtime schema compiler.
