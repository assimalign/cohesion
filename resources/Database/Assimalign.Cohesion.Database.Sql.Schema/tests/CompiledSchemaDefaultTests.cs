using System;
using System.Linq;
using System.Linq.Expressions;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

/// <summary>Defaults survive declarations, canonical documents, and migration plans.</summary>
public sealed class CompiledSchemaDefaultTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: literal text round-trips and contributes to identity")]
    [InlineData("7")]
    [InlineData("-1.25")]
    [InlineData("TRUE")]
    [InlineData("'it''s ready'")]
    [InlineData("NULL")]
    public void Serialize_DefaultLiteral_ShouldRoundTripAndChangeHash(string literal)
    {
        // Arrange: this thin contract keeps SQL text; the engine checks its literal kind and type.
        SqlCompiledSchema absent = Schema(null);
        SqlCompiledSchema declared = Schema(literal);

        // Act
        string document = declared.CanonicalDocument;
        SqlCompiledSchema restored = SqlCompiledSchemaSerializer.Deserialize(document);

        // Assert
        document.ShouldContain("\"defaultLiteral\":", Case.Sensitive);
        absent.CanonicalDocument.ShouldNotContain("defaultLiteral", Case.Sensitive);
        restored.Tables[0].Columns[0].DefaultLiteral.ShouldBe(literal);
        restored.CanonicalDocument.ShouldBe(document);
        restored.Hash.ShouldBe(declared.Hash);
        declared.Hash.ShouldNotBe(absent.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: absent defaults preserve the v6 canonical document")]
    public void Serialize_AbsentDefault_ShouldPreserveCanonicalDocument()
    {
        // Arrange
        const string document = "{\"format\":\"cohesion/database-schema/v9\",\"name\":\"defaults\",\"allowsDestructiveChanges\":false,\"types\":[],\"tables\":[{\"owner\":1,\"name\":\"items\",\"rowType\":\"Tests:Item\",\"columns\":[{\"name\":\"Value\",\"type\":4,\"isNullable\":true,\"maxLength\":null,\"precision\":null,\"scale\":null,\"customType\":null}],\"primaryKey\":null,\"indexes\":[],\"constraints\":[]}],\"principals\":[]}";

        // Act
        SqlCompiledSchema restored = SqlCompiledSchemaSerializer.Deserialize(document);

        // Assert
        restored.Tables[0].Columns[0].DefaultLiteral.ShouldBeNull();
        restored.CanonicalDocument.ShouldBe(document);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: blank literal documents name the invalid column")]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Create_BlankDefault_ShouldReportColumnPath(string literal)
    {
        // Act
        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() => Schema(literal));

        // Assert
        SqlSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(SqlSchemaValidationErrorCode.IncompleteDeclaration);
        error.Declaration.ShouldBe("tables.items.columns.Value.defaultLiteral");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: added tables and columns carry their defaults into plans")]
    public void Plan_DefaultedTableAndColumn_ShouldRetainLiterals()
    {
        // Arrange
        SqlCompiledSchema desired = Schema("7");
        SqlCompiledSchema initial = new(SqlCompiledSchema.CurrentFormat, "defaults", false, [],
            [new CompiledSchemaTable("items", "Tests:Item",
                [new CompiledSchemaColumn("Id", DatabaseType.Int32, false)], null, [], [])], []);
        SqlCompiledSchema expanded = new(SqlCompiledSchema.CurrentFormat, "defaults", false, [],
            [new CompiledSchemaTable("items", "Tests:Item",
                [new CompiledSchemaColumn("Id", DatabaseType.Int32, false), desired.Tables[0].Columns[0]], null, [], [])], []);

        // Act
        SqlSchemaMigrationOperation table = SqlSchemaMigrationPlanner.Plan(null, desired).Operations.ShouldHaveSingleItem();
        SqlSchemaMigrationOperation column = SqlSchemaMigrationPlanner.Plan(initial, expanded).Operations.ShouldHaveSingleItem();

        // Assert
        table.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AddTable);
        table.Table!.Columns[0].DefaultLiteral.ShouldBe("7");
        column.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AddColumn);
        column.Column!.DefaultLiteral.ShouldBe("7");
        column.Safety.ShouldBe(SqlSchemaMigrationSafety.Safe);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: adding changing and removing a default remain explicit column changes")]
    [InlineData(null, "7")]
    [InlineData("7", "9")]
    [InlineData("7", null)]
    [InlineData("'a'", "'A'")]
    public void Plan_ChangedDefault_ShouldRetainBothDefinitions(string? before, string? after)
    {
        // Arrange
        SqlCompiledSchema current = Schema(before);
        SqlCompiledSchema refused = Schema(after);
        SqlCompiledSchema desired = Schema(after, allowsDestructiveChanges: true);

        // Act
        SqlSchemaMigrationException exception = Should.Throw<SqlSchemaMigrationException>(() => SqlSchemaMigrationPlanner.Plan(current, refused));
        SqlSchemaMigrationOperation operation = SqlSchemaMigrationPlanner.Plan(current, desired).Operations.ShouldHaveSingleItem();

        // Assert: the current dialect refuses ALTER COLUMN, rather than dropping a default silently.
        exception.Message.ShouldContain("AlterColumn", Case.Sensitive);
        operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AlterColumn);
        operation.PreviousColumn!.DefaultLiteral.ShouldBe(before);
        operation.Column!.DefaultLiteral.ShouldBe(after);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: matching snapshots need no column operation")]
    public void Plan_MatchingDefaultWithDifferentMetadata_ShouldBeEmpty()
    {
        // Arrange: different destructive policies give different hashes, forcing structural comparison.
        SqlCompiledSchema current = Schema("7");
        SqlCompiledSchema desired = Schema("7", allowsDestructiveChanges: true);

        // Act
        SqlSchemaMigrationPlan plan = SqlSchemaMigrationPlanner.Plan(current, desired);

        // Assert
        current.Hash.ShouldNotBe(desired.Hash);
        plan.Operations.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: the builder retains literal text alongside named and domain column types")]
    public void Compile_DefaultDeclaration_ShouldPreserveColumnOrderTypesAndLiteralText()
    {
        // Arrange
        SqlSchema declaration = SqlSchema.Create("defaults", schema =>
        {
            schema.Type<Money>(type => type.Decimal(18, 2));
            schema.Table<Item>("items", table =>
            {
                table.DefaultSql(item => item.Id, "7");
                table.Key(item => item.Id);
                table.Column(item => item.Label, "VARCHAR(16)");
                table.DefaultSql(item => item.Label, "'it''s ready'");
                table.DefaultSql(item => item.Price, "1.25");
                table.DefaultSql(item => item.Price, "1.25");
            });
        });

        // Act
        SqlCompiledSchema compiled = declaration.Compile();

        // Assert
        var columns = compiled.Tables.ShouldHaveSingleItem().Columns;
        columns.Select(column => column.Name).ShouldBe(["Id", "Label", "Price"]);
        columns.Select(column => column.DefaultLiteral).ShouldBe(["7", "'it''s ready'", "1.25"]);
        columns[1].CustomType.ShouldBe("VARCHAR(16)");
        columns[2].CustomType.ShouldBe("Money");
        columns[2].Precision.ShouldBe(18);
        columns[2].Scale.ShouldBe(2);
        SqlCompiledSchemaSerializer.Deserialize(compiled.CanonicalDocument).Hash.ShouldBe(compiled.Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: the builder refuses null and blank literal text")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void DefaultSql_BlankLiteral_ShouldRefuseAtDeclaration(string? literal)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => SqlSchema.Create("defaults", schema =>
            schema.Table<Item>("items", table => table.DefaultSql(item => item.Id, literal!))));

        // Assert
        exception.ParamName.ShouldBe("sqlLiteral");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: the builder refuses conflicting defaults")]
    public void DefaultSql_ConflictingDeclaration_ShouldNameTheColumn()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => SqlSchema.Create("defaults", schema =>
            schema.Table<Item>("items", table =>
            {
                table.DefaultSql(item => item.Id, "7");
                table.DefaultSql(item => item.Id, "9");
            })));

        // Assert
        exception.ParamName.ShouldBe("sqlLiteral");
        exception.Message.ShouldContain("Column 'Id' is already declared", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema DEFAULT: selectors must be direct row members")]
    public void DefaultSql_InvalidSelector_ShouldRefuseAtDeclaration()
    {
        // Arrange
        Expression<Func<Item, object?>> calculated = item => item.Id + 1;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => SqlSchema.Create("defaults", schema =>
            schema.Table<Item>("items", table => table.DefaultSql(calculated, "7"))));
        ArgumentNullException absent = Should.Throw<ArgumentNullException>(() => SqlSchema.Create("defaults", schema =>
            schema.Table<Item>("items", table => table.DefaultSql(null!, "7"))));

        // Assert
        exception.ParamName.ShouldBe("selector");
        absent.ParamName.ShouldBe("selector");
    }

    private static SqlCompiledSchema Schema(string? literal, bool allowsDestructiveChanges = false)
        => new(SqlCompiledSchema.CurrentFormat, "defaults", allowsDestructiveChanges, [],
            [new CompiledSchemaTable("items", "Tests:Item",
                [new CompiledSchemaColumn("Value", DatabaseType.Int32, true, DefaultLiteral: literal)], null, [], [])], []);

    private readonly record struct Money(decimal Value);
    private sealed record Item(int Id, string Label, Money Price);
}
