using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public partial class CompileDatabaseSchemaTaskTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Type modifiers: named decimal arguments retain runtime precision and scale")]
    [InlineData(1, 28, false)]
    [InlineData(28, 1, true)]
    public void Execute_NamedDecimalArguments_ShouldRespectParameterNames(int precision, int scale, bool valid)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, $$"""
            using Assimalign.Cohesion.Database.Sql.Schema;
            var schema = SqlSchema.Create("app", schema => schema.Type<Money>(type => type.Decimal(scale: {{scale}}, precision: {{precision}})));
            public readonly record struct Money(decimal Value);
            """);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBe(valid, Errors(engine));
        if (valid)
        {
            var domain = SqlCompiledSchemaSerializer.Read(SchemaPath(task, "app")).Types.ShouldHaveSingleItem();
            domain.Precision.ShouldBe(precision);
            domain.Scale.ShouldBe(scale);
        }
        else
        {
            engine.Errors.ShouldContain(error => error.Code == "COHDBSDK106");
            task.Schemas.ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Type modifiers: extracted columns have runtime boundary and hash parity")]
    [MemberData(nameof(SqlTypeModifierCases.Columns), MemberType = typeof(SqlTypeModifierCases))]
    public void Execute_ModifierBoundary_ShouldMatchRuntime(string type, bool valid, string member)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, $$"""
            using Assimalign.Cohesion.Database.Sql.Schema;
            using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
            var schema = SqlSchema.Create("app", schema => schema.Table<SqlTypeModifierCases.Row>("items", table => table.Column(row => row.{{member}}, "{{type}}")));
            """);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);
        // The shared test row has the same CLR identity in the Roslyn compilation and runtime.
        task.ReferencePaths = [.. task.ReferencePaths, new Microsoft.Build.Utilities.TaskItem(typeof(SqlTypeModifierCases.Row).Assembly.Location)];

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBe(valid, Errors(engine));
        if (valid)
        {
            var runtime = SqlSchema.Compile("app", schema => schema.Table<SqlTypeModifierCases.Row>("items", table =>
                table.Column(SqlTypeModifierCases.Selector(member), type)));
            var extracted = SqlCompiledSchemaSerializer.Read(SchemaPath(task, "app"));
            extracted.CanonicalDocument.ShouldBe(runtime.CanonicalDocument);
            extracted.Hash.ShouldBe(runtime.Hash);
        }
        else
        {
            engine.Errors.ShouldContain(error => error.Code == "COHDBSDK106");
            Errors(engine).ShouldContain($"items.{member}", Case.Sensitive);
            task.Schemas.ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Type modifiers: extracted decimal domains use the declaration ranges")]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(28, 0, true)]
    [InlineData(28, 28, true)]
    [InlineData(28, 29, false)]
    [InlineData(29, 0, false)]
    [InlineData(28, -1, false)]
    public void Execute_DomainModifierBoundary_ShouldNameDomain(int precision, int scale, bool valid)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string sourcePath = directory.File("Schema.cs");
        File.WriteAllText(sourcePath, $$"""
            using Assimalign.Cohesion.Database.Sql.Schema;
            var schema = SqlSchema.Create("app", schema => schema.Type<Money>(type => type.Decimal({{precision}}, {{scale}})));
            public readonly record struct Money(decimal Value);
            """);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, sourcePath, "Sql", engine);

        // Act
        bool success = task.Execute();

        // Assert
        success.ShouldBe(valid, Errors(engine));
        if (!valid)
        {
            engine.Errors.ShouldContain(error => error.Code == "COHDBSDK106");
            Errors(engine).ShouldContain("Money", Case.Sensitive);
            task.Schemas.ShouldBeEmpty();
        }
    }
}
