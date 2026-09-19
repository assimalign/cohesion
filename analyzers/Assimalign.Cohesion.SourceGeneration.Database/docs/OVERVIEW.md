# Database source generation

`Assimalign.Cohesion.SourceGeneration.Database` emits entity readers, writers, identity access and
detached change snapshots from the application's existing `SqlSchema.Create` or `SqlSchema.Compile`
declarations. It extends the existing Database generator project and targets `netstandard2.0` under
the analyzers area's compiler-host exception. Emitted code targets the application's runtime and
uses the model-agnostic contracts in `Assimalign.Cohesion.Database.Mapping`.

The schema declaration remains the only mapping description. There are no mapping attributes,
parallel manifests, runtime registries or reflection fallback. The generator binds real
`ISqlSchemaBuilder` and `ISqlTableBuilder<T>` symbols, then follows inline callbacks or named
source-declared callbacks. Unsupported executable composition fails with a compiler diagnostic.

## Using the generator

The Database SDK supplies this generator through the Database framework analyzer payload. Set
`CohesionGenerateDatabaseMappers` to `true` in the application project to enable it. The SDK exposes
that property to Roslyn. This flag opts into code generation; the existing C# schema remains the
only database description. Missing, false or invalid values produce no mapper output or diagnostics,
so ordinary schema-only applications keep their existing behavior.

An in-repo consumer outside the Database SDK references `Assimalign.Cohesion.Database.Mapping`,
`Assimalign.Cohesion.Database.Sql.Schema` and a `CohesionAnalyzerReference` for
`Assimalign.Cohesion.SourceGeneration.Database`, and declares:

```xml
<PropertyGroup>
  <CohesionGenerateDatabaseMappers>true</CohesionGenerateDatabaseMappers>
</PropertyGroup>
<ItemGroup>
  <CompilerVisibleProperty Include="CohesionGenerateDatabaseMappers" />
</ItemGroup>
```

The mapping contracts must also be referenced for generation to activate.

For external package consumers, the Mapping NuGet package already bundles the generator. A plain
SDK project references the Mapping and SQL schema packages and sets both items shown above; no
separate generator package is needed. `Sdk.Database` users receive the generator from the targeting
pack and need only the property assignment.

```csharp
public sealed class Customer
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

private static ISqlSchema Declare() => SqlSchema.Create("sales", database =>
    database.Table<Customer>("customers", table =>
    {
        table.Key(customer => customer.Id);
        table.Column(customer => customer.Name);
    }));
```

This emits `CustomerMapper` in the entity's namespace. It implements
`IEntityMapper<Customer, int, CustomerMapper.Snapshot>`,
`IEntityReader<Customer, IReadOnlyList<object?>>` and
`IEntityWriter<Customer, IList<object?>>`. Values follow retained schema column order, so
`mapper.Read(new object?[] { 7, "Ada" })` materializes a customer. The SQL-shaped value sequence
belongs to this generated mapper; the runtime mapping core does not prescribe a row shape.

`Capture` produces an immutable snapshot exposing the selected members for persistence adapters.
Binary data is copied on capture, read, write and snapshot access. Undeclared members are ignored.
Snapshots detect value changes, binary content mutations, `DateTime.Kind` changes and
`DateTimeOffset.Offset` changes. There are no proxies, interception or navigation traversal.

## Verification and scope

Run the generator suite by project path:

```powershell
dotnet test analyzers/Assimalign.Cohesion.SourceGeneration.Database/tests/Assimalign.Cohesion.SourceGeneration.DatabaseTests.csproj
```

The tests compile and execute emitted code, execute the real `SqlSchema.Compile` API, compare its
column order, key and storage types, and round-trip entities. Runtime discovery is checked in
generated source. The NativeAOT guard lives under
`resources/Database/Assimalign.Cohesion.Database.Mapping/samples/` and executes the generated mapper
and unit of work after native publishing.

The existing schema builder uses CLR type/expression metadata when **executed**. That pre-existing
schema compilation path is outside this mapper's runtime path: the AOT guard's declaration method
is a compile-time input and is never invoked. This work proves static mapping after trimming; it
does not claim to replace or make every runtime schema-builder operation reflection-free.

See [DESIGN.md](DESIGN.md) for supported declarations, diagnostics and follow-on model boundaries.
