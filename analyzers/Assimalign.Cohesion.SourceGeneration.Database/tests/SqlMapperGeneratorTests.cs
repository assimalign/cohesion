using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.SourceGeneration.Database.Tests;

public sealed class SqlMapperGeneratorTests
{
    private const string Preamble = """
        #nullable enable
        using System;
        using System.Linq;
        using Assimalign.Cohesion.Database.Sql.Schema;
        using Assimalign.Cohesion.Database.Mapping;
        namespace GeneratedTest;
        public sealed class Entity
        {
            public long Id { get; set; }
            public string? Name { get; set; }
            public byte[]? Payload { get; set; }
            public int? Count { get; set; }
            public string Unmapped { get; set; } = "original";
        }
        """;

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: real compiled schema and mapper round-trip agree")]
    public void Generate_RealSchema_ShouldCompileExecuteAndMatchRetainedModel()
    {
        string source = Preamble + """

            public static class Probe
            {
                private static void Configure(ISqlSchemaBuilder database)
                    => database.Table<Entity>("entities", ConfigureTable);

                private static void ConfigureTable(ISqlTableBuilder<Entity> table)
                {
                    table.Column(entity => entity.Name);
                    table.Key(entity => entity.Id);
                    table.Column(entity => entity.Payload);
                    table.Index(entity => entity.Name);
                    table.Column(entity => entity.Count);
                }

                public static bool Run()
                {
                    SqlCompiledSchema schema = SqlSchema.Compile("sample", Configure);
                    var table = schema.Tables.Single();
                    if (!table.Columns.Select(column => column.Name).SequenceEqual(new[] { "Name", "Id", "Payload", "Count" })) return false;
                    if (table.PrimaryKey!.Columns.Single() != "Id") return false;
                    var mapper = new EntityMapper();
                    IEntityReader<Entity, System.Collections.Generic.IReadOnlyList<object?>> reader = mapper;
                    IEntityWriter<Entity, System.Collections.Generic.IList<object?>> writer = mapper;
                    var entity = new Entity { Id = 42, Name = "original", Payload = new byte[] { 1, 2 }, Count = null };
                    var values = new object?[table.Columns.Count];
                    writer.Write(entity, values);
                    var roundTrip = reader.Read(values);
                    if (mapper.GetKey(roundTrip) != 42 || roundTrip.Name != "original" || roundTrip.Count != null) return false;
                    if (!mapper.AreEqual(mapper.Capture(entity), mapper.Capture(roundTrip))) return false;
                    entity.Payload[0] = 9;
                    if (((byte[])values[2]!)[0] != 1 || roundTrip.Payload![0] != 1) return false;
                    ((byte[])values[2]!)[1] = 8;
                    return roundTrip.Payload[1] == 2;
                }
            }
            """;

        CompileAndRun(source).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: snapshots detect declared mutations and ignore undeclared members")]
    public void Generate_Snapshots_ShouldCompareDeclaredDetachedValues()
    {
        string source = Preamble + """

            public static class Probe
            {
                private static ISqlSchema Declare() => SqlSchema.Create("sample", database =>
                    database.Table<Entity>(table =>
                    {
                        table.PrimaryKey(entity => entity.Id);
                        table.Column(entity => entity.Name);
                        table.Column(entity => entity.Payload);
                        table.Column(entity => entity.Count);
                    }));

                public static bool Run()
                {
                    var mapper = new EntityMapper();
                    var entity = new Entity { Id = 7, Name = null, Payload = new byte[] { 3, 4 }, Count = 9 };
                    var baseline = mapper.Capture(entity);
                    entity.Unmapped = "ignored";
                    if (!mapper.AreEqual(baseline, mapper.Capture(entity))) return false;
                    entity.Payload = new byte[] { 3, 4 };
                    if (!mapper.AreEqual(baseline, mapper.Capture(entity))) return false;
                    entity.Payload[1] = 8;
                    if (mapper.AreEqual(baseline, mapper.Capture(entity))) return false;
                    entity.Payload[1] = 4;
                    entity.Name = "changed";
                    if (mapper.AreEqual(baseline, mapper.Capture(entity))) return false;
                    entity.Name = null;
                    entity.Count = null;
                    if (mapper.AreEqual(baseline, mapper.Capture(entity))) return false;
                    entity.Count = 9;
                    entity.Id = 8;
                    return !mapper.AreEqual(baseline, mapper.Capture(entity));
                }
            }
            """;

        CompileAndRun(source).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: named arguments and direct fields materialize correctly")]
    public void Generate_FieldsAndNamedArguments_ShouldCompileAndRun()
    {
        string source = """
            #nullable enable
            using Assimalign.Cohesion.Database.Sql.Schema;
            namespace GeneratedTest;
            public sealed class Entity
            {
                public System.Guid Id;
                public string @event { get; init; } = "";
                public System.DateOnly Date { get; set; }
            }
            public static class Probe
            {
                private static ISqlSchema Declare() => SqlSchema.Create(configure: database =>
                    database.Table<Entity>(configure: table =>
                    {
                        table.Key(entity => entity.Id);
                        table.Column(entity => entity.@event);
                        table.Column(entity => entity.Date);
                    }, name: "entities"), name: "sample");
                public static bool Run()
                {
                    var mapper = new EntityMapper();
                    var id = System.Guid.NewGuid();
                    var date = new System.DateOnly(2026, 9, 19);
                    var entity = mapper.Read(new object?[] { id, "event", date });
                    var values = new object?[3];
                    mapper.Write(entity, values);
                    return mapper.GetKey(entity) == id && (string)values[1]! == "event" && (System.DateOnly)values[2]! == date;
                }
            }
            """;

        CompileAndRun(source).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Mapping] - Generator: unsafe or dynamic declarations fail at compile time")]
    [InlineData("table.Column(entity => entity.Name);", "COHMAP003")]
    [InlineData("if (true) table.Key(entity => entity.Id);", "COHMAP001")]
    [InlineData("table.Key(entity => entity.Count);", "COHMAP003")]
    [InlineData("table.Key(entity => entity.Payload);", "COHMAP003")]
    [InlineData("table.Key(entity => entity.Id); table.Column(entity => entity.Name!.Length);", "COHMAP001")]
    public void Generate_UnsupportedDeclaration_ShouldReportDiagnostic(string body, string diagnostic)
    {
        string source = Preamble + "\npublic static class Declaration { public static ISqlSchema Create() => SqlSchema.Create(\"test\", database => database.Table<Entity>(table => { " + body + " })); }";

        Run(source).Diagnostics.ShouldContain(value => value.Id == diagnostic);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Mapping] - Generator: unsupported entity construction is diagnosed")]
    [InlineData("public sealed class Entity { public long Id { get; } }", "COHMAP002")]
    [InlineData("public sealed class Entity { public Entity(int value) {} public long Id { get; set; } }", "COHMAP002")]
    [InlineData("public sealed class Entity { public string? Id { get; set; } }", "COHMAP003")]
    [InlineData("public sealed class Entity { public object Id { get; set; } = new(); }", "COHMAP002")]
    [InlineData("public sealed class Entity { public long Id { get; set; } public required string Missing { get; set; } }", "COHMAP002")]
    public void Generate_UnsupportedEntity_ShouldReportDiagnostic(string entity, string diagnostic)
    {
        string source = "#nullable enable\nusing Assimalign.Cohesion.Database.Sql.Schema;\n" + entity +
            "\npublic static class Declaration { public static ISqlSchema Create() => SqlSchema.Create(\"test\", database => database.Table<Entity>(table => table.Key(entity => entity.Id))); }";

        Run(source).Diagnostics.ShouldContain(value => value.Id == diagnostic);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: conflicting declarations cannot produce divergent mappers")]
    public void Generate_ConflictingSchemas_ShouldReportDiagnosticAndEmitNothing()
    {
        string source = Preamble + """

            public static class Declaration
            {
                public static ISqlSchema One() => SqlSchema.Create("one", database => database.Table<Entity>(table => table.Key(entity => entity.Id)));
                public static ISqlSchema Two() => SqlSchema.Create("two", database => database.Table<Entity>(table => { table.Key(entity => entity.Id); table.Column(entity => entity.Name); }));
            }
            """;

        RunResult result = Run(source);

        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Id == "COHMAP004");
        result.Generated.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: matching repeated schema declarations share one mapper")]
    public void Generate_IdenticalSchemas_ShouldEmitOneMapper()
    {
        string source = Preamble + """

            public static class Declaration
            {
                public static ISqlSchema One() => SqlSchema.Create("one", database => database.Table<Entity>(table => table.Key(entity => entity.Id)));
                public static ISqlSchema Two() => SqlSchema.Create("two", database => database.Table<Entity>(table => table.Key(entity => entity.Id)));
            }
            """;

        RunResult result = Run(source);

        result.Diagnostics.ShouldBeEmpty();
        result.Generated.Count.ShouldBe(1);
        result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: generated mapper never emits runtime discovery")]
    public void Generate_StaticMapping_ShouldContainNoRuntimeDiscovery()
    {
        string source = Preamble + "\npublic static class Declaration { public static ISqlSchema Create() => SqlSchema.Create(\"test\", database => database.Table<Entity>(table => table.Key(entity => entity.Id))); }";

        string generated = Run(source).Generated.Single();

        foreach (string forbidden in new[] { "System.Reflection", "typeof(", ".GetType(", "Activator.", "Expression.", "IQueryable", "dynamic " })
        {
            generated.ShouldNotContain(forbidden, Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: scalar storage types and checked byte conversion match compiled schema")]
    public void Generate_AllSupportedScalars_ShouldRoundTripWithRetainedStorageTypes()
    {
        string source = """
            #nullable enable
            using System;
            using System.Linq;
            using Assimalign.Cohesion.Database.Sql.Schema;
            using static Assimalign.Cohesion.Database.Sql.Schema.SqlSchema;
            namespace GeneratedTest;
            public sealed class Entity
            {
                public int Id { get; set; }
                public bool Boolean { get; set; }
                public sbyte SignedByte { get; set; }
                public byte Byte { get; set; }
                public short Short { get; set; }
                public long Long { get; set; }
                public float Float { get; set; }
                public double Double { get; set; }
                public decimal Decimal { get; set; }
                public string? Text { get; set; }
                public byte[]? Binary { get; set; }
                public DateOnly Date { get; set; }
                public TimeOnly Time { get; set; }
                public DateTime DateTime { get; set; }
                public DateTimeOffset Offset { get; set; }
                public TimeSpan Span { get; set; }
                public Guid Guid { get; set; }
                public byte? NullableByte { get; set; }
            }
            public static class Probe
            {
                public static bool Run()
                {
                    var schema = Compile("scalar", database => database.Table<Entity>(table =>
                    {
                        table.Key(row => row.Id);
                        table.Column(row => row.Boolean); table.Column(row => row.SignedByte); table.Column(row => row.Byte);
                        table.Column(row => row.Short); table.Column(row => row.Long); table.Column(row => row.Float);
                        table.Column(row => row.Double); table.Column(row => row.Decimal); table.Column(row => row.Text);
                        table.Column(row => row.Binary); table.Column(row => row.Date); table.Column(row => row.Time);
                        table.Column(row => row.DateTime); table.Column(row => row.Offset); table.Column(row => row.Span);
                        table.Column(row => row.Guid); table.Column(row => row.NullableByte);
                    }));
                    if (!schema.Tables.Single().Columns.Select(column => column.Type.ToString()).SequenceEqual(new[]
                        { "Int32", "Boolean", "Int8", "Int16", "Int16", "Int64", "Float32", "Float64", "Decimal", "String", "Binary", "Date", "Time", "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Int16" })) return false;
                    var entity = new Entity
                    {
                        Id = 1, Boolean = true, SignedByte = -12, Byte = 255, Short = -234, Long = 1234567890123,
                        Float = 1.25f, Double = -3.5, Decimal = 123.456m, Text = null, Binary = new byte[] { 1, 2 },
                        Date = new DateOnly(2026, 9, 19), Time = new TimeOnly(12, 30), DateTime = new DateTime(2026, 9, 19),
                        Offset = new DateTimeOffset(2026, 9, 19, 12, 30, 0, TimeSpan.Zero), Span = TimeSpan.FromSeconds(12),
                        Guid = Guid.NewGuid(), NullableByte = 254
                    };
                    var mapper = new EntityMapper();
                    var snapshot = mapper.Capture(entity);
                    snapshot.Binary![0] = 8;
                    if (snapshot.Binary[0] != 1) return false;
                    var values = new object?[18];
                    mapper.Write(entity, values);
                    if ((short)values[3]! != 255 || (short)values[17]! != 254) return false;
                    if (!mapper.AreEqual(snapshot, mapper.Capture(mapper.Read(values)))) return false;
                    values[17] = null;
                    if (mapper.Read(values).NullableByte != null) return false;
                    values[3] = (short)256;
                    try { mapper.Read(values); return false; } catch (OverflowException) { }
                    values[3] = (short)-1;
                    try { mapper.Read(values); return false; } catch (OverflowException) { }
                    return true;
                }
            }
            """;

        CompileAndRun(source).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Mapping] - Generator: snapshot member collisions are diagnosed")]
    [InlineData("Snapshot")]
    [InlineData("Matches")]
    [InlineData("BytesEqual")]
    [InlineData("_value0")]
    public void Generate_ReservedSnapshotMember_ShouldReportDiagnostic(string name)
    {
        string source = "using Assimalign.Cohesion.Database.Sql.Schema; public sealed class Entity { public long Id { get; set; } public int " + name +
            " { get; set; } } public static class Declaration { public static ISqlSchema Create() => SqlSchema.Create(\"test\", database => database.Table<Entity>(table => { table.Key(row => row.Id); table.Column(row => row." + name + "); })); }";

        Run(source).Diagnostics.ShouldContain(diagnostic => diagnostic.Id == "COHMAP002");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: nested entity mapper name collisions are diagnosed")]
    public void Generate_CollidingEntityNames_ShouldReportDiagnostic()
    {
        const string source = """
            using Assimalign.Cohesion.Database.Sql.Schema;
            public class Outer { public class Inner { public int Id { get; set; } } }
            public class Outer_Inner { public int Id { get; set; } }
            public static class Declaration
            {
                public static ISqlSchema Create() => SqlSchema.Create("test", database =>
                {
                    database.Table<Outer.Inner>(table => table.Key(row => row.Id));
                    database.Table<Outer_Inner>(table => table.Key(row => row.Id));
                });
            }
            """;

        RunResult result = Run(source);

        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Id == "COHMAP004");
        result.Generated.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: temporal snapshots detect kind and offset changes including nullable members")]
    public void Generate_TemporalState_ShouldCompareStoredRepresentation()
    {
        const string source = """
            #nullable enable
            using System;
            using Assimalign.Cohesion.Database.Sql.Schema;
            namespace GeneratedTest;
            public sealed class Entity
            {
                public int Id { get; set; }
                public DateTime Date { get; set; }
                public DateTimeOffset Offset { get; set; }
                public DateTime? NullableDate { get; set; }
                public DateTimeOffset? NullableOffset { get; set; }
            }
            public static class Probe
            {
                private static ISqlSchema Declare() => SqlSchema.Create("test", database => database.Table<Entity>(table =>
                {
                    table.Key(row => row.Id); table.Column(row => row.Date); table.Column(row => row.Offset);
                    table.Column(row => row.NullableDate); table.Column(row => row.NullableOffset);
                }));
                public static bool Run()
                {
                    var mapper = new EntityMapper();
                    var date = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
                    var offset = new DateTimeOffset(date);
                    var entity = new Entity { Id = 1, Date = date, Offset = offset, NullableDate = date, NullableOffset = offset };
                    var original = mapper.Capture(entity);
                    entity.Date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                    if (mapper.AreEqual(original, mapper.Capture(entity))) return false;
                    entity.Date = date;
                    entity.Offset = offset.ToOffset(TimeSpan.FromHours(2));
                    if (mapper.AreEqual(original, mapper.Capture(entity))) return false;
                    entity.Offset = offset;
                    entity.NullableDate = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                    if (mapper.AreEqual(original, mapper.Capture(entity))) return false;
                    entity.NullableDate = date;
                    entity.NullableOffset = offset.ToOffset(TimeSpan.FromHours(2));
                    if (mapper.AreEqual(original, mapper.Capture(entity))) return false;
                    entity.NullableOffset = offset;
                    if (!mapper.AreEqual(original, mapper.Capture(entity))) return false;
                    entity.NullableDate = null;
                    entity.NullableOffset = null;
                    var nullable = mapper.Capture(entity);
                    return !mapper.AreEqual(original, nullable) && mapper.AreEqual(nullable, mapper.Capture(entity));
                }
            }
            """;

        CompileAndRun(source).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Mapping] - Generator: long entity names produce bounded deterministic source filenames")]
    public void Generate_LongEntityName_ShouldBoundSourceHint()
    {
        string source = Preamble.Replace("namespace GeneratedTest;", "namespace LongNamespace" + new string('x', 100) + ";") +
            "\npublic static class Declaration { public static ISqlSchema Create() => SqlSchema.Create(\"test\", database => database.Table<Entity>(table => table.Key(entity => entity.Id))); }";

        RunResult first = Run(source);
        RunResult second = Run(source);

        first.Diagnostics.ShouldBeEmpty();
        first.Hints.Single().Length.ShouldBeLessThan(140);
        first.Hints.Single().ShouldBe(second.Hints.Single());
    }

    [Theory(DisplayName = "Cohesion Test [Database.Mapping] - Generator: absent or disabled generation leaves schema-only applications untouched")]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("invalid")]
    public void Generate_DisabledOption_ShouldEmitNothingAndReportNoDiagnostics(string? option)
    {
        string source = Preamble + """

            public static class Declaration
            {
                public static ISqlSchema Create() => SqlSchema.Create("test", database =>
                {
                    if (DateTime.UtcNow.Year > 2000)
                        database.Table<Entity>(table => table.Column(entity => entity.Name));
                });
            }
            """;

        RunResult result = Run(source, option);

        result.Diagnostics.ShouldBeEmpty();
        result.Generated.ShouldBeEmpty();
        result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    private static bool CompileAndRun(string source)
    {
        RunResult result = Run(source);
        result.Diagnostics.ShouldBeEmpty();
        using var output = new MemoryStream();
        var emitted = result.Compilation.Emit(output);
        emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        emitted.Success.ShouldBeTrue();
        output.Position = 0;
        // Reflection belongs to this JIT-only Roslyn test harness, never the emitted/runtime mapper.
        var loadContext = new AssemblyLoadContext(Guid.NewGuid().ToString(), isCollectible: true);
        try
        {
            Assembly assembly = loadContext.LoadFromStream(output);
            return (bool)assembly.GetType("GeneratedTest.Probe")!.GetMethod("Run")!.Invoke(null, null)!;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static RunResult Run(string source, string? generateMappers = "true")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
        foreach (Assembly assembly in new[]
                 {
                     typeof(Assimalign.Cohesion.Database.Sql.Schema.SqlSchema).Assembly,
                     typeof(Assimalign.Cohesion.Database.Mapping.IEntityMapper<,,>).Assembly,
                     typeof(Assimalign.Cohesion.Database.EngineModel).Assembly,
                     typeof(Assimalign.Cohesion.Database.Types.DatabaseType).Assembly
                 })
        {
            if (!references.Any(reference => reference.Display == assembly.Location))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        CSharpCompilation compilation = CSharpCompilation.Create("GeneratedSqlMapper_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new SqlMapperGenerator().AsSourceGenerator() },
            parseOptions: parseOptions, optionsProvider: new TestOptionsProvider(generateMappers));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out _);
        GeneratorDriverRunResult generated = driver.GetRunResult();
        return new RunResult(updated, generated.Diagnostics.ToArray(), generated.GeneratedTrees.Select(tree => tree.ToString()).ToArray(),
            generated.Results.SelectMany(result => result.GeneratedSources).Select(result => result.HintName).ToArray());
    }

    private sealed record RunResult(Compilation Compilation, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Generated, IReadOnlyList<string> Hints);

    private sealed class TestOptionsProvider(string? generateMappers) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestOptions(generateMappers);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestOptions(null);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new TestOptions(null);
    }

    private sealed class TestOptions(string? generateMappers) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = generateMappers ?? string.Empty;
            return key == "build_property.CohesionGenerateDatabaseMappers" && generateMappers is not null;
        }
    }
}
