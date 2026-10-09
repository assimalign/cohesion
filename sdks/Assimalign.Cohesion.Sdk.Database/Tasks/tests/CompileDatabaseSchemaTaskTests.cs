using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

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

        task.Execute().ShouldBeTrue(Errors(engine));

        string schemaPath = SchemaPath(task, "orders");
        SqlCompiledSchema schema = SqlCompiledSchemaSerializer.Read(schemaPath);
        schema.Format.ShouldBe(SqlCompiledSchema.CurrentFormat);
        schema.Name.ShouldBe("orders");
        schema.Tables.Select(static table => table.Name).ShouldBe(["OrderLines", "Orders"]);
        ITaskItem item = task.Schemas.ShouldHaveSingleItem();
        item.ItemSpec.ShouldBe("orders");
        item.GetMetadata("Hash").ShouldBe(schema.Hash);
        item.GetMetadata("SchemaPath").ShouldBe(schemaPath);
        item.GetMetadata("HashPath").ShouldBe(HashPath(task, "orders"));
        File.ReadAllText(HashPath(task, "orders")).ShouldBe(schema.Hash + "\n");
        SqlCompiledSchemaSerializer.Serialize(schema).ShouldBe(File.ReadAllText(schemaPath));
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
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
        task.Schemas.ShouldBeEmpty();
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

        task.Execute().ShouldBeTrue(Errors(engine));

        SqlCompiledSchema schema = SqlCompiledSchemaSerializer.Read(SchemaPath(task, "sample"));
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
            database.Table<ParityOrder>("Orders", table =>
            {
                table.Key(order => order.Id);
                table.Column(order => order.Note);
                table.Column(order => order.Total);
                table.Index(order => order.Note);
            });
        });
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("ParitySchema.cs");
        File.WriteAllText(sourcePath, ParitySchemaSource);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeTrue(Errors(engine));

        File.ReadAllText(SchemaPath(task, "parity")).ShouldBe(runtimeSchema.CanonicalDocument);
        task.Schemas.ShouldHaveSingleItem().GetMetadata("Hash").ShouldBe(runtimeSchema.Hash);
        File.ReadAllText(HashPath(task, "parity")).ShouldBe(runtimeSchema.Hash + "\n");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: each declared database writes its own artifact, in parity with the runtime")]
    public void Execute_WithEngineBuilderDeclarations_ShouldWriteOneArtifactPerDatabase()
    {
        // The runtime's SqlDatabaseBuilder.Schema(declare) is SqlSchema.Create(database name, declare).
        SqlCompiledSchema runtimeSales = SqlSchema.Compile("sales", schema =>
            schema.Table<SalesOrder>("orders", table =>
            {
                table.Key(order => order.Id);
                table.Column(order => order.Item);
                table.Index(order => order.Item);
            }));
        SqlCompiledSchema runtimeReporting = SqlSchema.Compile("reporting", schema =>
            schema.Table<DailyTotal>("daily_totals", table => table.Key(total => total.Day)));
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Program.cs");
        File.WriteAllText(sourcePath, EngineBuilderSource);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeTrue(Errors(engine));

        task.Schemas.Select(static item => item.ItemSpec).ShouldBe(["reporting", "sales"]);
        File.ReadAllText(SchemaPath(task, "sales")).ShouldBe(runtimeSales.CanonicalDocument);
        File.ReadAllText(HashPath(task, "sales")).ShouldBe(runtimeSales.Hash + "\n");
        File.ReadAllText(SchemaPath(task, "reporting")).ShouldBe(runtimeReporting.CanonicalDocument);
        File.ReadAllText(HashPath(task, "reporting")).ShouldBe(runtimeReporting.Hash + "\n");
        Directory.GetFiles(task.OutputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(
            ["reporting.schema.json", "reporting.schema.sha256", "sales.schema.json", "sales.schema.sha256", "schemas.manifest"]);
        File.ReadAllText(Path.Combine(task.OutputDirectory, "schemas.manifest")).ShouldBe("reporting\nsales\n");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a database no longer declared loses its artifacts")]
    public void Execute_AfterDatabaseRenamed_ShouldRemoveUndeclaredArtifacts()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Program.cs");
        File.WriteAllText(sourcePath, EngineBuilderSource);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);
        task.Execute().ShouldBeTrue(Errors(engine));
        File.WriteAllText(directory.File("obj/cohesion/database/notes.txt"), "not an artifact");

        // A consumer's own file that happens to look like an artifact, in an overridden directory.
        File.WriteAllText(directory.File("obj/cohesion/database/appsettings.schema.json"), "{}");

        File.WriteAllText(sourcePath, EngineBuilderSource.Replace("\"sales\"", "\"ledger\"", StringComparison.Ordinal));
        task = CreateTask(directory, sourcePath, "Sql", engine);
        task.Execute().ShouldBeTrue(Errors(engine));

        // Only the artifacts the previous manifest listed are the task's to delete.
        task.Schemas.Select(static item => item.ItemSpec).ShouldBe(["ledger", "reporting"]);
        File.Exists(SchemaPath(task, "sales")).ShouldBeFalse();
        File.Exists(HashPath(task, "sales")).ShouldBeFalse();
        File.Exists(SchemaPath(task, "ledger")).ShouldBeTrue();
        File.Exists(directory.File("obj/cohesion/database/notes.txt")).ShouldBeTrue();
        File.Exists(directory.File("obj/cohesion/database/appsettings.schema.json")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(task.OutputDirectory, "schemas.manifest")).ShouldBe("ledger\nreporting\n");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a principal or a custom type, which every engine build refuses, fails the build")]
    [InlineData("""database.Principal("app", principal => principal.Grant(SqlPermission.Read, "Orders"));""", "Principal")]
    [InlineData("""database.Type<Money>(type => type.Decimal(18, 2));""", "Type")]
    public void Execute_WithUnprovisionableDeclaration_ShouldFailWithoutArtifacts(string declaration, string method)
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SchemaTypes + $$"""

            public static class SchemaProgram
            {
                public static void Configure()
                {
                    SqlSchema.Create("orders", database =>
                    {
                        database.Table<Order>("Orders", table => table.Key(order => order.Id));
                        {{declaration}}
                    });
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK108" &&
            error.Message != null &&
            error.Message.StartsWith($"SqlSchemaBuilder.{method} declares what no SQL engine can provision yet", StringComparison.Ordinal) &&
            error.Message.Contains("COHSQLP001", StringComparison.Ordinal));
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a column of a type SQL cannot store names the supported types")]
    public void Execute_WithColumnOfUnsupportedType_ShouldNameTheSupportedTypes()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SchemaTypes + """

            public static class SchemaProgram
            {
                public static void Configure()
                {
                    SqlSchema.Create("billing", database =>
                        database.Table<Invoice>("Invoices", table =>
                        {
                            table.Key(invoice => invoice.Id);
                            table.Column(invoice => invoice.Total);
                        }));
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        BuildErrorEventArgs error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBSDK106");
        error.Message.ShouldNotBeNull().ShouldContain("Column 'Invoices.Total' has type '", Case.Sensitive);
        error.Message.ShouldContain("which is not a SQL column type", Case.Sensitive);
        error.Message.ShouldNotContain("Type<", Case.Sensitive);
    }

    /// <summary>
    /// <c>table.Check(name, sql)</c> extracts as constant strings into the same document and hash the
    /// runtime compiler writes, a CHECK that calls a function the engine registers included: native
    /// functions are invisible to the SDK, and the engine's build validates the predicate.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a CHECK extracts as constant text in parity with the runtime, native functions included")]
    public void Execute_WithTableChecks_ShouldMatchRuntimeCompilerDocumentAndHash()
    {
        SqlCompiledSchema runtimeSchema = SqlSchema.Compile("checked", schema =>
            schema.Table<SalesOrder>("orders", table =>
            {
                table.Key(order => order.Id);
                table.Column(order => order.Item);
                table.Check("ck_item", "LENGTH(Item) > 0 AND slugify(Item) <> ''");
                table.Check("ck_id", "Id >= 0");
            }));
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Program.cs");
        File.WriteAllText(sourcePath, """
            using System;
            using Assimalign.Cohesion.Database.Sql;
            using Assimalign.Cohesion.Database.Sql.Schema;

            namespace Assimalign.Cohesion.Sdk.Database.Tests
            {
                public sealed record SalesOrder(long Id, string Item);

                public static class CheckedProgram
                {
                    private const string ItemCheck = "LENGTH(Item) > 0 AND " + "slugify(Item) <> ''";

                    public static SqlDatabaseEngineBuilder Configure()
                    {
                        SqlDatabaseEngineBuilder sql = SqlDatabaseEngine.CreateBuilder("checked-sql");
                        sql.Functions.Add(SqlScalarFunction.Create("slugify",
                            static (string text) => text.ToLowerInvariant(), SqlFunctionVolatility.Immutable));
                        sql.AddDatabase("checked", database => database.Schema(schema =>
                            schema.Table<SalesOrder>("orders", table =>
                            {
                                table.Key(order => order.Id);
                                table.Column(order => order.Item);
                                table.Check("ck_item", ItemCheck);
                                table.Check(sql: "Id >= 0", name: "ck_id");
                            })));
                        return sql;
                    }
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeTrue(Errors(engine));

        File.ReadAllText(SchemaPath(task, "checked")).ShouldBe(runtimeSchema.CanonicalDocument);
        task.Schemas.ShouldHaveSingleItem().GetMetadata("Hash").ShouldBe(runtimeSchema.Hash);
        File.ReadAllText(HashPath(task, "checked")).ShouldBe(runtimeSchema.Hash + "\n");
        SqlCompiledSchemaSerializer.Read(SchemaPath(task, "checked")).Tables.ShouldHaveSingleItem().Constraints
            .Select(static constraint => (constraint.Name, constraint.Kind, constraint.Expression?.CanonicalText))
            .ShouldBe([
                ("ck_id", CompiledSchemaConstraintKind.Check, "Id >= 0"),
                ("ck_item", CompiledSchemaConstraintKind.Check, "LENGTH(Item) > 0 AND slugify(Item) <> ''"),
            ]);
    }

    /// <summary>A CHECK whose name or text the build cannot read as a constant, or whose name is taken, fails the build.</summary>
    /// <param name="declaration">The CHECK declaration.</param>
    /// <param name="code">The diagnostic code.</param>
    /// <param name="message">A fragment of its message.</param>
    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a CHECK needs constant text and a name of its own")]
    [InlineData("""table.Check("ck_item", Names.Predicate);""", "COHDBSDK104", "CHECK 'ck_item' on table 'orders' needs its SQL predicate as a non-empty compile-time string constant")]
    [InlineData("""table.Check(Names.Check, "Id > 0");""", "COHDBSDK102", "A CHECK on table 'orders' needs a non-empty compile-time string constant for its name.")]
    [InlineData("""table.Check("ck", "Id > 0"); table.Check("CK", "Id < 9");""", "COHDBSDK105", "Table 'orders' declares CHECK 'CK', a name another of its constraints")]
    [InlineData("""table.Check("PK_orders", "Id > 0");""", "COHDBSDK105", "Table 'orders' declares CHECK 'PK_orders'")]
    public void Execute_WithUnreadableOrDuplicateCheck_ShouldFail(string declaration, string code, string message)
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Program.cs");
        File.WriteAllText(sourcePath, $$"""
            using System;
            using Assimalign.Cohesion.Database.Sql.Schema;

            public sealed record SalesOrder(long Id, string Item);

            public static class Names
            {
                public static readonly string Predicate = "Id > 0";
                public static readonly string Check = "ck";
            }

            public static class Program
            {
                public static void Configure()
                {
                    SqlSchema.Create("sales", schema => schema.Table<SalesOrder>("orders", table =>
                    {
                        table.Key(order => order.Id);
                        {{declaration}}
                    }));
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error => error.Code == code && error.Message != null && error.Message.Contains(message, StringComparison.Ordinal),
            Errors(engine));
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: the hosted AddSql shape declares its databases")]
    public void Execute_WithHostedEngineVerb_ShouldReadTheEnclosingDatabaseName()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Program.cs");
        File.WriteAllText(sourcePath, HostedSource);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeTrue(Errors(engine));

        ITaskItem item = task.Schemas.ShouldHaveSingleItem();
        item.ItemSpec.ShouldBe("Sample");
        SqlCompiledSchema schema = SqlCompiledSchemaSerializer.Read(SchemaPath(task, "Sample"));
        schema.Name.ShouldBe("Sample");
        schema.Tables.ShouldHaveSingleItem().Name.ShouldBe("orders");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a database builder schema needs a constant enclosing AddDatabase name")]
    [InlineData("""sql.AddDatabase(Names.Sales, database => database.Schema(schema => schema.Table<SalesOrder>("orders", table => table.Key(order => order.Id))));""")]
    [InlineData("""
        sql.AddDatabase("sales", Declare);
        static void Declare(SqlDatabaseBuilder database)
            => database.Schema(schema => schema.Table<SalesOrder>("orders", table => table.Key(order => order.Id)));
        """)]
    [InlineData("""sql.AddDatabase("sales", database => other.Schema(schema => schema.Table<SalesOrder>("orders", table => table.Key(order => order.Id))));""")]
    public void Execute_WithUnanalyzableDatabaseName_ShouldReportNamedDiagnostic(string declaration)
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Program.cs");
        File.WriteAllText(sourcePath, $$"""
            using System;
            using Assimalign.Cohesion.Database.Sql;
            using Assimalign.Cohesion.Database.Sql.Schema;

            public sealed record SalesOrder(long Id, string Item);

            public static class Names
            {
                public static readonly string Sales = "sales";
            }

            public static class Program
            {
                public static void Configure(SqlDatabaseEngineBuilder sql, SqlDatabaseBuilder other)
                {
                    {{declaration}}
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK102" &&
            error.Message != null &&
            error.Message.Contains("SqlDatabaseEngineBuilder.AddDatabase(name, configure)", StringComparison.Ordinal));
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a database name that cannot name a file is refused")]
    [InlineData("sales/eu")]
    [InlineData("sales:eu")]
    [InlineData("sales.")]
    public void Execute_WithDatabaseNameUnfitForFile_ShouldFail(string name)
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Program.cs");
        File.WriteAllText(sourcePath, EngineBuilderSource.Replace("\"sales\"", $"\"{name}\"", StringComparison.Ordinal));
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK102" &&
            error.Message != null &&
            error.Message.Contains($"Database name '{name}' cannot name its schema artifact", StringComparison.Ordinal));
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
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
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: mismatched reference type fails precisely")]
    public void Execute_WithMismatchedReferenceType_ShouldFailPrecisely()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SqlSchemaSource.Replace(
            "record OrderLine(long Id, long OrderId, decimal Total)",
            "record OrderLine(long Id, string OrderId, decimal Total)",
            StringComparison.Ordinal));
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK106" &&
            error.Message != null &&
            error.Message.Contains("does not match primary key", StringComparison.Ordinal));
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: at least one schema declaration is required")]
    public void Execute_WithoutSchemaDeclaration_ShouldFail()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SchemaTypes);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK101" &&
            error.Message != null &&
            error.Message.Contains("found 0", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: one schema declaration per database name")]
    [InlineData("""SqlSchema.Create("orders", database => { });""")]
    [InlineData("""SqlSchema.Create("ORDERS", database => { });""")]
    [InlineData("""sql.AddDatabase("Orders", database => database.Schema(schema => { }));""")]
    public void Execute_WithTwoDeclarationsOfOneDatabase_ShouldFail(string second)
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, SqlSchemaSource + $$"""

            public static class SecondSchema
            {
                public static void Configure(Assimalign.Cohesion.Database.Sql.SqlDatabaseEngineBuilder sql)
                {
                    {{second}}
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);

        task.Execute().ShouldBeFalse();

        engine.Errors.ShouldContain(error =>
            error.Code == "COHDBSDK101" &&
            error.Message != null &&
            error.Message.Contains("has more than one schema declaration", StringComparison.Ordinal));
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
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
            .Append(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.Sql.dll"))
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
            OutputDirectory = directory.File("obj/cohesion/database")
        };
    }

    private static string SchemaPath(CompileDatabaseSchemaTask task, string database)
        => Path.GetFullPath(Path.Combine(task.OutputDirectory, database + ".schema.json"));

    private static string HashPath(CompileDatabaseSchemaTask task, string database)
        => Path.GetFullPath(Path.Combine(task.OutputDirectory, database + ".schema.sha256"));

    private static string Errors(RecordingBuildEngine engine)
        => string.Join(Environment.NewLine, engine.Errors.Select(static error => $"{error.Code}: {error.Message}"));

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
            public sealed record ParityOrder(long Id, string Note, decimal Total);

            public static class ParitySchemaProgram
            {
                public static void Configure()
                {
                    SqlSchema.Create("parity", database =>
                    {
                        database.AllowDestructiveChanges();
                        database.Table<ParityOrder>("Orders", table =>
                        {
                            table.Key(order => order.Id);
                            table.Column(order => order.Note);
                            table.Column(order => order.Total);
                            table.Index(order => order.Note);
                        });
                    });
                }
            }
        }
        """;

    // The engine-level composition of the extensibility design (§3.1): an inline schema anchored on
    // SqlDatabaseBuilder.Schema and a reusable SqlSchema value passed to AddDatabase(SqlSchema).
    private const string EngineBuilderSource = """
        using System;
        using Assimalign.Cohesion.Database.Sql;
        using Assimalign.Cohesion.Database.Sql.Schema;

        namespace Assimalign.Cohesion.Sdk.Database.Tests
        {
            public sealed record SalesOrder(long Id, string Item);
            public sealed record DailyTotal(DateOnly Day, decimal Amount);

            public static class EngineProgram
            {
                public static SqlDatabaseEngineBuilder Configure()
                {
                    SqlDatabaseEngineBuilder sql = SqlDatabaseEngine.CreateBuilder("orders-sql");
                    sql.Options.RootPath = "data";
                    sql.AddDatabase("sales", database =>
                    {
                        database.Provisioning = SqlProvisioningMode.Apply;
                        database.Schema(schema =>
                        {
                            schema.Table<SalesOrder>("orders", table =>
                            {
                                table.Key(order => order.Id);
                                table.Column(order => order.Item);
                                table.Index(order => order.Item);
                            });
                        });
                    });
                    sql.AddDatabase(ReportingSchema.Declaration);
                    sql.AddDatabase("archive");
                    return sql;
                }
            }

            public static class ReportingSchema
            {
                public static readonly SqlSchema Declaration = SqlSchema.Create("reporting", schema =>
                    schema.Table<DailyTotal>("daily_totals", table => table.Key(total => total.Day)));
            }
        }
        """;

    private const string HostedSource = """
        using System;
        using Assimalign.Cohesion.Database;
        using Assimalign.Cohesion.Database.Sql;

        public sealed record SampleOrder(long Id, string Item);

        public static class HostedProgram
        {
            private const string Database = "Sample";

            public static void Configure(IDatabaseApplicationBuilder builder)
            {
                builder.AddSql("sample-sql", sql =>
                {
                    sql.Options.RootPath = "data";
                    sql.AddServer(server => { });
                    sql.AddDatabase(Database, database => database.Schema(schema =>
                    {
                        schema.Table<SampleOrder>("orders", table =>
                        {
                            table.Key(order => order.Id);
                            table.Column(order => order.Item);
                        });
                    }));
                });
            }
        }
        """;

    private const string SchemaTypes = """
        using System;
        using Assimalign.Cohesion.Database;
        using Assimalign.Cohesion.Database.Sql.Schema;

        public sealed record Order(long Id, long CustomerId);
        public sealed record OrderLine(long Id, long OrderId, decimal Total);
        public sealed record MissingOrder(long Id);
        public sealed record Session(string Id, string Value);
        public sealed record Invoice(long Id, Money Total);
        public readonly record struct Money(decimal Amount);
        """;
}

public sealed record ParityOrder(long Id, string Note, decimal Total);

public sealed record SalesOrder(long Id, string Item);

public sealed record DailyTotal(DateOnly Day, decimal Amount);
