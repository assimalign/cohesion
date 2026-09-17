using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Sdk.Database.Tasks;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public class CompileDatabaseSchemaTaskTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: C# SQL declaration writes canonical document and hash")]
    public void Execute_WithSqlCSharpSchema_ShouldWriteCanonicalDocumentAndHash()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SqlSchemaSource);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        SqlCompiledSchema schema = SqlCompiledSchemaSerializer.Read(task.OutputPath);
        schema.Format.ShouldBe(SqlCompiledSchema.CurrentFormat);
        schema.Name.ShouldBe("orders");
        schema.Model.ShouldBe(EngineModel.Sql);
        schema.Tables.Select(static table => table.Name).ShouldBe(["OrderLines", "Orders"]);
        schema.Hash.ShouldBe(task.SchemaHash);
        File.ReadAllText(task.HashOutputPath).ShouldBe(schema.Hash + "\n");
        SqlCompiledSchemaSerializer.Serialize(schema).ShouldBe(File.ReadAllText(task.OutputPath));
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: key-value compilation fails until its model package exists")]
    public void Execute_WithKeyValuePairModel_ShouldFailWithoutArtifacts()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SqlSchemaSource);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "KeyValuePair", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK106" &&
            error.Message != null &&
            error.Message.Contains("model-specific compiled-schema package", StringComparison.Ordinal));
        File.Exists(task.OutputPath).ShouldBeFalse();
        File.Exists(task.HashOutputPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: reference-pack primitives use runtime type identities")]
    public void Execute_WithReferencePackPrimitives_ShouldUseRuntimeTypeIdentities()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, PrimitiveSchemaSource);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(
            directory,
            sourcePath,
            "Sql",
            engine,
            GetFrameworkReferenceAssemblyPaths());

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        SqlCompiledSchema schema = SqlCompiledSchemaSerializer.Read(task.OutputPath);
        CompiledSchemaTable table = schema.Tables.ShouldHaveSingleItem();
        (string Name, DatabaseType Type)[] expectedColumns =
        [
            ("Id", DatabaseType.Int64),
            ("Boolean", DatabaseType.Boolean),
            ("Byte", DatabaseType.Int16),
            ("SByte", DatabaseType.Int8),
            ("Int16", DatabaseType.Int16),
            ("Int32", DatabaseType.Int32),
            ("Float32", DatabaseType.Float32),
            ("Float64", DatabaseType.Float64),
            ("Decimal", DatabaseType.Decimal),
            ("Text", DatabaseType.String),
            ("Binary", DatabaseType.Binary),
            ("Date", DatabaseType.Date),
            ("Time", DatabaseType.Time),
            ("Timestamp", DatabaseType.DateTime),
            ("Offset", DatabaseType.DateTimeOffset),
            ("Duration", DatabaseType.TimeSpan),
            ("Identifier", DatabaseType.Guid)
        ];
        foreach ((string name, DatabaseType type) in expectedColumns)
        {
            table.Columns.Single(column => column.Name == name).Type.ShouldBe(type);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: static and runtime compilers have canonical parity")]
    public void Execute_WithSupportedSchema_ShouldMatchRuntimeCompilerDocumentAndHash()
    {
        SqlCompiledSchema runtimeSchema = SqlSchema.Compile("parity", database =>
        {
            database.AllowDestructiveChanges();
            database.Type<ParityMoney>(type => type.Decimal(18, 2));
            database.Type<ParityArrayHolder>(type => type.Decimal(9, 0));
            database.Table<ParityOrder>("Orders", table =>
            {
                table.Key(order => order.Id);
                table.Column(order => order.Note);
                table.Column(order => order.Total);
                table.Index(order => order.Note);
            });
            database.Function("next_order", (long orderId) => orderId + 1);
            database.Function("constant_sum", () => 1 + 2);
            database.Function("negative", () => -1L);
            database.Function<long>("wide_constant", () => 1);
            database.Function<long?, long?>("nullable_identity", value => value);
            database.Function<decimal?, decimal?>("nullable_decimal", value => value + value);
            database.Function<decimal, decimal>("round_decimal", value => decimal.Round(value, 2));
            database.Function<string, string>("suffix", value => value + "!");
            database.Function<int, string>("mixed_suffix", value => "x" + value);
            database.Function<ParityArrayHolder, long>("first_array_value", holder => holder.Values[0]);
            database.Trigger<ParityOrder>(SqlTriggerEvent.AfterInsert, (context, order) => context.Audit("created", order.Id));
            database.Extension("collation", "ordinal");
            database.Principal("app", principal =>
            {
                principal.Grant(SqlPermission.Read, "Orders");
                principal.Grant(SqlPermission.Read, "next_order");
            });
        });
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("ParitySchema.cs");
        File.WriteAllText(sourcePath, ParitySchemaSource);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, engine.Errors.Select(static error => error.Message)));

        File.ReadAllText(task.OutputPath).ShouldBe(SqlCompiledSchemaSerializer.Serialize(runtimeSchema));
        task.SchemaHash.ShouldBe(runtimeSchema.Hash);
        File.ReadAllText(task.HashOutputPath).ShouldBe(runtimeSchema.Hash + "\n");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: unsupported expression reports a named diagnostic")]
    public void Execute_WithUnsupportedExpression_ShouldReportNamedDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, ParitySchemaSource.Replace(
            "orderId + 1",
            "new Random().Next() + orderId",
            StringComparison.Ordinal));
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error => error.Code == "COHDBSDK107");
        File.Exists(task.OutputPath).ShouldBeFalse();
        File.Exists(task.HashOutputPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: dangling reference fails without artifacts")]
    public void Execute_WithDanglingReference_ShouldFailWithoutArtifacts()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SqlSchemaSource.Replace(
            "table.References<Order>(line => line.OrderId);",
            "table.References<MissingOrder>(line => line.OrderId);",
            StringComparison.Ordinal));
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK106" &&
            error.Message != null &&
            error.Message.Contains("undeclared table", StringComparison.Ordinal));
        File.Exists(task.OutputPath).ShouldBeFalse();
        File.Exists(task.HashOutputPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: mismatched reference type fails precisely")]
    public void Execute_WithMismatchedReferenceType_ShouldFailPrecisely()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SqlSchemaSource.Replace(
            "record OrderLine(long Id, long OrderId, Money Total)",
            "record OrderLine(long Id, string OrderId, Money Total)",
            StringComparison.Ordinal));
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK106" &&
            error.Message != null &&
            error.Message.Contains("does not match primary key", StringComparison.Ordinal));
        File.Exists(task.OutputPath).ShouldBeFalse();
        File.Exists(task.HashOutputPath).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: exactly one SqlSchema declaration is required")]
    [InlineData("", 0)]
    [InlineData("""
        public static class SecondSchema
        {
            public static void Configure()
            {
                SqlSchema.Create("second", database => { });
            }
        }
        """, 2)]
    public void Execute_WithoutExactlyOneSchemaDeclaration_ShouldFail(string replacement, int expectedCount)
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        string source = replacement.Length == 0
            ? SchemaTypes
            : SqlSchemaSource + Environment.NewLine + replacement;
        File.WriteAllText(sourcePath, source);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK101" &&
            error.Message != null &&
            error.Message.Contains($"found {expectedCount}", StringComparison.Ordinal));
    }

    private static CompileDatabaseSchemaTask CreateTask(
        TemporaryDirectory directory,
        string sourcePath,
        string model,
        RecordingBuildEngine engine,
        string[]? frameworkReferencePaths = null)
    {
        string[] frameworkReferences = frameworkReferencePaths ??
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string[] references = frameworkReferences
            .Append(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.dll"))
            .Append(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.Sql.Schema.dll"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CompileDatabaseSchemaTask
        {
            BuildEngine = engine,
            SourceFiles = [new TaskItem(sourcePath)],
            ReferencePaths = references.Select(static path => (ITaskItem)new TaskItem(path)).ToArray(),
            LanguageVersion = "preview",
            AssemblyName = typeof(CompileDatabaseSchemaTaskTests).Assembly.GetName().Name!,
            Model = model,
            ProjectDirectory = directory.Path,
            OutputPath = directory.File("obj/database.schema.json"),
            HashOutputPath = directory.File("obj/database.schema.sha256")
        };
    }

    private static string[] GetFrameworkReferenceAssemblyPaths()
    {
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        DirectoryInfo dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent ??
            throw new DirectoryNotFoundException("Could not locate the dotnet installation root.");
        string targetFramework = $"net{Environment.Version.Major}.0";
        string packRoot = Path.Combine(dotnetRoot.FullName, "packs", "Microsoft.NETCore.App.Ref");
        string? referenceDirectory = Directory.EnumerateDirectories(packRoot)
            .Where(path => Path.GetFileName(path).StartsWith($"{Environment.Version.Major}.", StringComparison.Ordinal))
            .Select(path => Path.Combine(path, "ref", targetFramework))
            .Where(Directory.Exists)
            .OrderByDescending(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (referenceDirectory is null)
        {
            throw new DirectoryNotFoundException($"Could not locate the {targetFramework} reference assemblies under '{packRoot}'.");
        }

        return Directory.GetFiles(referenceDirectory, "*.dll", SearchOption.TopDirectoryOnly);
    }

    private const string SqlSchemaSource = SchemaTypes + """

        public static class SchemaProgram
        {
            public static void Configure()
            {
                SqlSchema.Compile("orders", database =>
                {
                    database.Type<Money>(type => type.Decimal(18, 2));
                    database.Table<Order>("Orders", table =>
                    {
                        table.Key(order => order.Id);
                        table.Index(order => order.CustomerId);
                    });
                    database.Table<OrderLine>("OrderLines", table =>
                    {
                        table.Key(line => line.Id);
                        table.References<Order>(line => line.OrderId);
                        table.Column(line => line.Total);
                    });
                    database.Function("next_order", (long orderId) => orderId + 1);
                    database.Principal("app", principal => principal.Grant(SqlPermission.ReadWrite, "Orders", "OrderLines"));
                });
            }
        }
        """;

    private const string PrimitiveSchemaSource = """
        using System;
        using Assimalign.Cohesion.Database;
        using Assimalign.Cohesion.Database.Sql.Schema;

        public sealed record Order(
            long Id,
            bool Boolean,
            byte Byte,
            sbyte SByte,
            short Int16,
            int Int32,
            float Float32,
            double Float64,
            decimal Decimal,
            string Text,
            byte[] Binary,
            DateOnly Date,
            TimeOnly Time,
            DateTime Timestamp,
            DateTimeOffset Offset,
            TimeSpan Duration,
            Guid Identifier);

        public static class SchemaProgram
        {
            public static void Configure()
            {
                SqlSchema.Create("sample", database =>
                {
                    database.Table<Order>("Orders", table =>
                    {
                        table.Key(order => order.Id);
                        table.Column(order => order.Boolean);
                        table.Column(order => order.Byte);
                        table.Column(order => order.SByte);
                        table.Column(order => order.Int16);
                        table.Column(order => order.Int32);
                        table.Column(order => order.Float32);
                        table.Column(order => order.Float64);
                        table.Column(order => order.Decimal);
                        table.Column(order => order.Text);
                        table.Column(order => order.Binary);
                        table.Column(order => order.Date);
                        table.Column(order => order.Time);
                        table.Column(order => order.Timestamp);
                        table.Column(order => order.Offset);
                        table.Column(order => order.Duration);
                        table.Column(order => order.Identifier);
                    });
                });
            }
        }
        """;

    private const string ParitySchemaSource = """
        using System;
        using Assimalign.Cohesion.Database;
        using Assimalign.Cohesion.Database.Sql.Schema;

        namespace Assimalign.Cohesion.Sdk.Database.Tests
        {
            public sealed record ParityOrder(long Id, string Note, ParityMoney Total);
            public sealed record ParityArrayHolder(long[] Values);
            public readonly record struct ParityMoney(decimal Amount);

            public static class ParitySchemaProgram
            {
                public static void Configure()
                {
                        SqlSchema.Create("parity", database =>
                    {
                        database.AllowDestructiveChanges();
                        database.Type<ParityMoney>(type => type.Decimal(18, 2));
                        database.Type<ParityArrayHolder>(type => type.Decimal(9, 0));
                        database.Table<ParityOrder>("Orders", table =>
                        {
                            table.Key(order => order.Id);
                            table.Column(order => order.Note);
                            table.Column(order => order.Total);
                            table.Index(order => order.Note);
                        });
                        database.Function("next_order", (long orderId) => orderId + 1);
                        database.Function("constant_sum", () => 1 + 2);
                        database.Function("negative", () => -1L);
                        database.Function<long>("wide_constant", () => 1);
                        database.Function<long?, long?>("nullable_identity", value => value);
                        database.Function<decimal?, decimal?>("nullable_decimal", value => value + value);
                        database.Function<decimal, decimal>("round_decimal", value => decimal.Round(value, 2));
                        database.Function<string, string>("suffix", value => value + "!");
                        database.Function<int, string>("mixed_suffix", value => "x" + value);
                        database.Function<ParityArrayHolder, long>("first_array_value", holder => holder.Values[0]);
                        database.Trigger<ParityOrder>(SqlTriggerEvent.AfterInsert, (context, order) => context.Audit("created", order.Id));
                        database.Extension("collation", "ordinal");
                        database.Principal("app", principal =>
                        {
                            principal.Grant(SqlPermission.Read, "Orders");
                            principal.Grant(SqlPermission.Read, "next_order");
                        });
                    });
                }
            }
        }
        """;

    private const string SchemaTypes = """
        using System;
        using Assimalign.Cohesion.Database;
        using Assimalign.Cohesion.Database.Sql.Schema;

        public sealed record Order(long Id, long CustomerId);
        public sealed record OrderLine(long Id, long OrderId, Money Total);
        public sealed record MissingOrder(long Id);
        public sealed record Session(string Id, string Value);
        public readonly record struct Money(decimal Amount);
        """;
}

public sealed record ParityOrder(long Id, string Note, ParityMoney Total);

public sealed record ParityArrayHolder(long[] Values);

public readonly record struct ParityMoney(decimal Amount);
