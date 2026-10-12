using System;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Sdk.Database.Tasks;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public partial class CompileDatabaseSchemaTaskTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: boxed parenthesized selectors match runtime document and hash")]
    public void Execute_WithBoxedParenthesizedSelectors_ShouldMatchRuntimeDocumentAndHash()
    {
        // Arrange
        SqlCompiledSchema runtime = SqlSchema.Compile("defaults", schema => schema.Table<DefaultSelectorRow>(table =>
        {
            table.Default<object>(row => (object)(row.Number), 7);
            table.Default<object>(row => (object)(long)(row.Number), 7L);
            table.Default<object>(row => (object)(row.Character), 'A');
            table.Column(row => ((object)(row.Number)));
        }));
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, """
            using Assimalign.Cohesion.Database.Sql.Schema;
            namespace Assimalign.Cohesion.Sdk.Database.Tests;
            public sealed record DefaultSelectorRow(int Number, char Character);
            public static class Program
            {
                public static void Configure() => SqlSchema.Compile("defaults", schema => schema.Table<DefaultSelectorRow>(table =>
                {
                    table.Default<object>(row => (object)(row.Number), 7);
                    table.Default<object>(row => (object)(long)(row.Number), 7L);
                    table.Default<object>(row => (object)(row.Character), 'A');
                    table.Column(row => ((object)(row.Number)));
                }));
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBeTrue(Errors(engine));
        File.ReadAllText(SchemaPath(task, "defaults")).ShouldBe(runtime.CanonicalDocument);
        task.Schemas.ShouldHaveSingleItem().GetMetadata("Hash").ShouldBe(runtime.Hash);
        CompiledSchemaColumn number = SqlCompiledSchemaSerializer.Read(SchemaPath(task, "defaults")).Tables.ShouldHaveSingleItem()
            .Columns.Single(static column => column.Name == "Number");
        number.Type.ShouldBe(DatabaseType.Int32);
        number.DefaultLiteral.ShouldBe("7");
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: checked and unchecked selectors match runtime document and hash")]
    public void Execute_WithCheckedAndUncheckedSelectors_ShouldMatchRuntimeDocumentAndHash()
    {
        // Arrange
        SqlCompiledSchema runtime = SqlSchema.Compile("defaults", schema => schema.Table<DefaultSelectorRow>(table =>
        {
            table.Default<short>(row => checked((short)row.Number), (short)7);
            table.Default<short>(row => (short)(unchecked((short)(row.Number))), (short)7);
            table.Column(row => (object)(checked((char)(unchecked(row.Character)))));
            table.Column(row => checked((row)).Number);
            table.Column(row => unchecked((row)).Character);
            table.Column(row => unchecked((object)(checked((int)(row.Number)))), "int");
            table.Key(row => checked((object)(unchecked((long)(row.Number)))));
            table.PrimaryKey(row => (object)(unchecked((long)(checked(row.Number)))));
            table.Index(row => unchecked((object)(checked((char)(row.Character)))));
            table.DefaultSql(row => checked((object)(unchecked((char)(row.Character)))), "'A'");
            table.References<DefaultSelectorRow>(row => unchecked((object)(checked((long)(row.Number)))));
        }));
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, """
            using Assimalign.Cohesion.Database.Sql.Schema;
            namespace Assimalign.Cohesion.Sdk.Database.Tests;
            public sealed record DefaultSelectorRow(int Number, char Character);
            public static class Program
            {
                public static void Configure() => SqlSchema.Compile("defaults", schema => schema.Table<DefaultSelectorRow>(table =>
                {
                    table.Default<short>(row => checked((short)row.Number), (short)7);
                    table.Default<short>(row => (short)(unchecked((short)(row.Number))), (short)7);
                    table.Column(row => (object)(checked((char)(unchecked(row.Character)))));
                    table.Column(row => checked((row)).Number);
                    table.Column(row => unchecked((row)).Character);
                    table.Column(row => unchecked((object)(checked((int)(row.Number)))), "int");
                    table.Key(row => checked((object)(unchecked((long)(row.Number)))));
                    table.PrimaryKey(row => (object)(unchecked((long)(checked(row.Number)))));
                    table.Index(row => unchecked((object)(checked((char)(row.Character)))));
                    table.DefaultSql(row => checked((object)(unchecked((char)(row.Character)))), "'A'");
                    table.References<DefaultSelectorRow>(row => unchecked((object)(checked((long)(row.Number)))));
                }));
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBeTrue(Errors(engine));
        File.ReadAllText(SchemaPath(task, "defaults")).ShouldBe(runtime.CanonicalDocument);
        task.Schemas.ShouldHaveSingleItem().GetMetadata("Hash").ShouldBe(runtime.Hash);
        File.ReadAllText(HashPath(task, "defaults")).ShouldBe(runtime.Hash + "\n");
        CompiledSchemaColumn number = SqlCompiledSchemaSerializer.Read(SchemaPath(task, "defaults")).Tables.ShouldHaveSingleItem()
            .Columns.Single(static column => column.Name == "Number");
        number.Type.ShouldBe(DatabaseType.Int32);
        number.DefaultLiteral.ShouldBe("7");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: a captured row member is refused by runtime and source compilation")]
    [InlineData("Default")]
    [InlineData("DefaultSql")]
    [InlineData("Column")]
    public void Execute_WithCapturedRowSelector_ShouldMatchRuntimeRefusal(string operation)
    {
        // Arrange
        var other = new SalesOrder(1, "captured");
        Action<SqlTableBuilder<SalesOrder>> configure = operation switch
        {
            "Default" => table => table.Default(row => other.Item, "pending"),
            "DefaultSql" => table => table.DefaultSql(row => other.Item, "'pending'"),
            _ => table => table.Column(row => other.Item)
        };
        string argument = operation switch
        {
            "Default" => ", \"pending\"",
            "DefaultSql" => ", \"'pending'\"",
            _ => string.Empty
        };
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, $$"""
            using Assimalign.Cohesion.Database.Sql.Schema;
            public sealed record SalesOrder(long Id, string Item);
            public static class Program
            {
                public static void Configure()
                {
                    var other = new SalesOrder(1, "captured");
                    SqlSchema.Compile("defaults", schema => schema.Table<SalesOrder>(table =>
                    {
                        table.Key(row => row.Id);
                        table.{{operation}}(row => other.Item{{argument}});
                    }));
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        ArgumentException runtimeError = Should.Throw<ArgumentException>(() =>
            SqlSchema.Compile("defaults", schema => schema.Table(configure)));
        bool success = task.Execute();

        // Assert
        runtimeError.ParamName.ShouldBe("selector");
        success.ShouldBeFalse();
        var error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBSDK106");
        error.Message.ShouldNotBeNull().ShouldContain("must select a direct member", Case.Sensitive);
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: nullable custom domain default matches runtime")]
    public void Execute_WithNullDomainDefault_ShouldMatchRuntimeDocumentAndHash()
    {
        // Arrange
        SqlCompiledSchema runtime = SqlSchema.Compile("defaults", schema =>
        {
            schema.Type<Price>(type => type.Decimal(18, 2));
            schema.Table<NullableDomainRow>(table => table.Default(row => row.Value, null));
        });
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, """
            using Assimalign.Cohesion.Database.Sql.Schema;
            namespace Assimalign.Cohesion.Sdk.Database.Tests;
            public readonly record struct Price(decimal Amount);
            public sealed record NullableDomainRow(Price? Value);
            public static class Program
            {
                public static void Configure() => SqlSchema.Compile("defaults", schema =>
                {
                    schema.Type<Price>(type => type.Decimal(18, 2));
                    schema.Table<NullableDomainRow>(table => table.Default(row => row.Value, null));
                });
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBeTrue(Errors(engine));
        File.ReadAllText(SchemaPath(task, "defaults")).ShouldBe(runtime.CanonicalDocument);
        task.Schemas.ShouldHaveSingleItem().GetMetadata("Hash").ShouldBe(runtime.Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: references compare mapped storage and SQL type metadata")]
    [InlineData("int", "Status", "", true)]
    [InlineData("string", "char", "", true)]
    [InlineData("int", "Status", "table.Column(row => row.Id, \"state\");", false)]
    public void Execute_WithMappedReferenceTypes_ShouldRespectStorageAndDomain(string targetType, string sourceType, string targetColumn, bool expected)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, $$"""
            using Assimalign.Cohesion.Database.Sql.Schema;
            public enum Status { Pending = 2 }
            public sealed record Target({{targetType}} Id);
            public sealed record Source({{sourceType}} TargetId);
            public static class Program
            {
                public static void Configure() => SqlSchema.Compile("defaults", schema =>
                {
                    schema.Table<Target>(table => { table.Key(row => row.Id); {{targetColumn}} });
                    schema.Table<Source>(table => table.References<Target>(row => row.TargetId));
                });
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBe(expected, Errors(engine));
        if (!expected)
        {
            engine.Errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBSDK106");
            Directory.Exists(task.OutputDirectory).ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: typed constants match runtime document and hash")]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_WithTypedConstantDefaults_ShouldMatchRuntimeDocumentAndHash(bool useReferencePack)
    {
        // Arrange
        SqlCompiledSchema runtime = SqlSchema.Compile("defaults", schema => schema.Table<DefaultConstantRow>("defaults", table =>
        {
            table.Default(row => row.Text, "it's " + "ready");
            table.Default(row => row.Empty, "");
            table.Default(row => row.Character, '\'');
            table.Default(row => row.SByte, (sbyte)-8);
            table.Default(row => row.Byte, (byte)255);
            table.Default(row => row.Int16, (short)-300);
            table.Default(row => row.Int32, -(2 + 5));
            table.Default(row => row.Int64, 7);
            table.Default(row => row.Float32, 16777217);
            table.Default(row => row.Float64, -1.25);
            table.Default(row => row.Decimal, -12.3400m);
            table.Default(row => row.DecimalFromInteger, 7);
            table.Default(row => row.Float32FromCharacter, 'A');
            table.Default(row => row.Float64FromCharacter, 'A');
            table.Default(row => row.DecimalFromCharacter, 'A');
            table.Default(row => row.True, true);
            table.Default(row => row.False, false);
            table.Default(row => row.OptionalNumber, null);
            table.Default<int?>(row => row.OptionalValue, 7);
            table.Default(row => row.OptionalText, null);
            table.Default(row => row.Enum, DefaultStatus.Pending);
            table.Default(row => row.EnumCast, (DefaultStatus)3);
            table.Default(row => row.EnumSByte, DefaultSByte.Negative);
            table.Default(row => row.EnumByte, DefaultByte.Maximum);
            table.Default(row => row.EnumInt16, DefaultInt16.Negative);
            table.Default(row => row.EnumUInt16, DefaultUInt16.Maximum);
            table.Default(row => row.EnumUInt32, DefaultUInt32.Maximum);
            table.Default(row => row.EnumInt64, DefaultInt64.Negative);
            table.Default(row => row.EnumUInt64, DefaultUInt64.Maximum);
            table.DefaultSql(row => row.RawDate, "'2026-01-01'");
            table.DefaultSql(row => row.Text, "'it''s ready'");
        }));
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, TypedDefaultsSource);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine,
            useReferencePack ? GetFrameworkReferenceAssemblyPaths() : null);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBeTrue(Errors(engine));
        File.ReadAllText(SchemaPath(task, "defaults")).ShouldBe(runtime.CanonicalDocument);
        task.Schemas.ShouldHaveSingleItem().GetMetadata("Hash").ShouldBe(runtime.Hash);
        File.ReadAllText(HashPath(task, "defaults")).ShouldBe(runtime.Hash + "\n");
        var defaults = SqlCompiledSchemaSerializer.Read(SchemaPath(task, "defaults")).Tables.ShouldHaveSingleItem().Columns
            .ToDictionary(static column => column.Name, static column => column.DefaultLiteral);
        defaults["Text"].ShouldBe("'it''s ready'");
        defaults["Empty"].ShouldBe("''");
        defaults["Character"].ShouldBe("''''");
        defaults["Int32"].ShouldBe("-7");
        defaults["Decimal"].ShouldBe("-12.3400");
        defaults["Float32FromCharacter"].ShouldBe("65");
        defaults["Float64FromCharacter"].ShouldBe("65");
        defaults["DecimalFromCharacter"].ShouldBe("65");
        defaults["True"].ShouldBe("TRUE");
        defaults["False"].ShouldBe("FALSE");
        defaults["OptionalNumber"].ShouldBe("NULL");
        defaults["OptionalValue"].ShouldBe("7");
        defaults["OptionalText"].ShouldBe("NULL");
        defaults["Enum"].ShouldBe("2");
        defaults["EnumCast"].ShouldBe("3");
        defaults["EnumUInt64"].ShouldBe("18446744073709551615");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: unevaluated typed values name DefaultSql without writing artifacts")]
    [InlineData("string", "Environment.MachineName")]
    [InlineData("string", "ReadValue()")]
    [InlineData("int", "quantity")]
    [InlineData("Guid", "Guid.NewGuid()")]
    [InlineData("DateOnly", "new DateOnly(2026, 1, 1)")]
    [InlineData("DateTime", "DateTime.UnixEpoch")]
    public void Execute_WithNonconstantTypedDefault_ShouldReportEscapeHatch(string type, string expression)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, $$"""
            using System;
            using Assimalign.Cohesion.Database.Sql.Schema;

            public sealed record Row({{type}} Value);
            public static class Program
            {
                private static string ReadValue() => "pending";
                public static void Configure()
                {
                    int quantity = 7;
                    SqlSchema.Compile("defaults", schema => schema.Table<Row>(table => table.Default(row => row.Value, {{expression}})));
                }
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBeFalse();
        var error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBSDK109");
        error.Message.ShouldNotBeNull().ShouldContain("DefaultSql", Case.Sensitive);
        error.File.ShouldBe(sourcePath);
        error.LineNumber.ShouldBeGreaterThan(0);
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
        task.Schemas.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: equivalent typed and raw literals have the same document and hash")]
    public void Execute_WithEquivalentDefaultSql_ShouldProduceSameDocumentAndHash()
    {
        // Arrange
        using var typedDirectory = new TemporaryDirectory();
        using var rawDirectory = new TemporaryDirectory();
        const string source = """
            using Assimalign.Cohesion.Database.Sql.Schema;
            public sealed record Row(string Value);
            public static class Program
            {
                public static void Configure() => SqlSchema.Compile("defaults", schema =>
                    schema.Table<Row>(table => table.Default(value: "it's ready", selector: row => row.Value)));
            }
            """;
        File.WriteAllText(typedDirectory.File("Schema.cs"), source);
        File.WriteAllText(rawDirectory.File("Schema.cs"), source.Replace("Default(value: \"it's ready\"", "DefaultSql(sqlLiteral: \"'it''s ready'\"", StringComparison.Ordinal));
        var typedEngine = new RecordingBuildEngine();
        var rawEngine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask typedTask = CreateTask(typedDirectory, typedDirectory.File("Schema.cs"), "Sql", typedEngine);
        CompileDatabaseSchemaTask rawTask = CreateTask(rawDirectory, rawDirectory.File("Schema.cs"), "Sql", rawEngine);

        // Act
        bool typedSuccess = typedTask.Execute();
        bool rawSuccess = rawTask.Execute();

        // Assert
        typedSuccess.ShouldBeTrue(Errors(typedEngine));
        rawSuccess.ShouldBeTrue(Errors(rawEngine));
        File.ReadAllText(SchemaPath(typedTask, "defaults")).ShouldBe(File.ReadAllText(SchemaPath(rawTask, "defaults")));
        typedTask.Schemas.ShouldHaveSingleItem().GetMetadata("Hash").ShouldBe(rawTask.Schemas.ShouldHaveSingleItem().GetMetadata("Hash"));
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Compile schema: typed defaults refuse a mismatch or conflicting literal")]
    [InlineData("table.Default<object>(row => row.Value, \"7\");", "COHDBSDK106")]
    [InlineData("table.Default(row => row.Value, 7); table.Default(row => row.Value, 8);", "COHDBSDK105")]
    public void Execute_WithInvalidTypedDefault_ShouldFailWithoutArtifacts(string declaration, string code)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, $$"""
            using Assimalign.Cohesion.Database.Sql.Schema;
            public sealed record Row(int Value);
            public static class Program
            {
                public static void Configure() => SqlSchema.Compile("defaults", schema =>
                    schema.Table<Row>(table => { {{declaration}} }));
            }
            """);
        var engine = new RecordingBuildEngine();
        CompileDatabaseSchemaTask task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBeFalse();
        engine.Errors.ShouldHaveSingleItem().Code.ShouldBe(code);
        Directory.Exists(task.OutputDirectory).ShouldBeFalse();
        task.Schemas.ShouldBeEmpty();
    }

    private const string TypedDefaultsSource = """
        using System;
        using Assimalign.Cohesion.Database.Sql.Schema;

        namespace Assimalign.Cohesion.Sdk.Database.Tests;

        public sealed record DefaultConstantRow(
            string Text, string Empty, char Character, sbyte SByte, byte Byte, short Int16,
            int Int32, long Int64, float Float32, double Float64, decimal Decimal,
            decimal DecimalFromInteger, float Float32FromCharacter, double Float64FromCharacter,
            decimal DecimalFromCharacter, bool True, bool False, int? OptionalNumber,
            int? OptionalValue, string? OptionalText, DefaultStatus Enum, DefaultStatus EnumCast,
            DefaultSByte EnumSByte, DefaultByte EnumByte, DefaultInt16 EnumInt16,
            DefaultUInt16 EnumUInt16, DefaultUInt32 EnumUInt32, DefaultInt64 EnumInt64,
            DefaultUInt64 EnumUInt64, DateOnly RawDate);
        public enum DefaultStatus { Pending = 2 }
        public enum DefaultSByte : sbyte { Negative = -8 }
        public enum DefaultByte : byte { Maximum = byte.MaxValue }
        public enum DefaultInt16 : short { Negative = -300 }
        public enum DefaultUInt16 : ushort { Maximum = ushort.MaxValue }
        public enum DefaultUInt32 : uint { Maximum = uint.MaxValue }
        public enum DefaultInt64 : long { Negative = -7000000000 }
        public enum DefaultUInt64 : ulong { Maximum = ulong.MaxValue }
        public static class Program
        {
            private const string Text = "it's " + "ready";
            public static void Configure() => SqlSchema.Compile("defaults", schema => schema.Table<DefaultConstantRow>("defaults", table =>
            {
                table.Default(value: Text, selector: row => row.Text);
                table.Default(row => row.Empty, "");
                table.Default(row => row.Character, '\'');
                table.Default(row => row.SByte, (sbyte)-8);
                table.Default(row => row.Byte, (byte)255);
                table.Default(row => row.Int16, (short)-300);
                table.Default(row => row.Int32, -(2 + 5));
                table.Default(row => row.Int64, 7);
                table.Default(row => row.Float32, 16777217);
                table.Default(row => row.Float64, -1.25);
                table.Default(row => row.Decimal, -12.3400m);
                table.Default(row => row.DecimalFromInteger, 7);
                table.Default(row => row.Float32FromCharacter, 'A');
                table.Default(row => row.Float64FromCharacter, 'A');
                table.Default(row => row.DecimalFromCharacter, 'A');
                table.Default(row => row.True, true);
                table.Default(row => row.False, false);
                table.Default(row => row.OptionalNumber, null);
                table.Default<int?>(row => row.OptionalValue, 7);
                table.Default(row => row.OptionalText, null);
                table.Default(row => row.Enum, DefaultStatus.Pending);
                table.Default(row => row.EnumCast, (DefaultStatus)3);
                table.Default(row => row.EnumSByte, DefaultSByte.Negative);
                table.Default(row => row.EnumByte, DefaultByte.Maximum);
                table.Default(row => row.EnumInt16, DefaultInt16.Negative);
                table.Default(row => row.EnumUInt16, DefaultUInt16.Maximum);
                table.Default(row => row.EnumUInt32, DefaultUInt32.Maximum);
                table.Default(row => row.EnumInt64, DefaultInt64.Negative);
                table.Default(row => row.EnumUInt64, DefaultUInt64.Maximum);
                table.DefaultSql(row => row.RawDate, "'2026-01-01'");
                table.DefaultSql(sqlLiteral: "'it''s ready'", selector: row => row.Text);
            }));
        }
        """;
}
