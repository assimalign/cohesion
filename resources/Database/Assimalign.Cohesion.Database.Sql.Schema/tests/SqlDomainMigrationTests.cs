using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public sealed class SqlDomainMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Schema] - Migration: domain widening requires opt-in because runtime application casts are unknown")]
    public void Plan_DomainWideningWithoutOptIn_ShouldRefuseUnprovenConversion()
    {
        // Arrange
        SqlCompiledSchema Declare(int precision, int scale) => SqlSchema.Create("typed", schema =>
        {
            schema.Type<Amount>(type => type.Decimal(precision, scale));
            schema.Table<Row>("items", table => table.Column(row => row.Value));
        }).Compile();

        // Act / Assert
        Should.Throw<SqlSchemaMigrationException>(() => SqlSchemaMigrationPlanner.Plan(Declare(8, 2), Declare(9, 3)));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Schema] - Migration: same-name domain facets produce referring column changes")]
    public void Plan_DomainScaleChange_ShouldCarryEffectivePreviousAndTargetTypes()
    {
        // Arrange
        SqlCompiledSchema Declare(int scale) => SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Type<Amount>(type => type.Decimal(8, scale));
            schema.Table<Row>("items", table => table.Column(row => row.Value));
        }).Compile();

        // Act
        var plan = SqlSchemaMigrationPlanner.Plan(Declare(3), Declare(2));

        // Assert
        var change = plan.Operations.Single(operation => operation.Kind == SqlSchemaMigrationOperationKind.AlterColumn);
        change.PreviousColumn!.Scale.ShouldBe(3);
        change.Column!.Scale.ShouldBe(2);

        // The portable plan also names partial-index rebuilding around the effective
        // domain change, even though the column's raw CustomType name is unchanged.
        SqlCompiledSchema Partial(SqlCompiledSchema schema) => new(schema.Format, schema.Name, true, schema.Types,
            schema.Tables.Select(table => new CompiledSchemaTable(table.Name, table.RowType, table.Columns, table.PrimaryKey,
                [new("ix_value", ["Value"], false, "Value > 0")], table.Constraints)).ToArray(), []);
        var indexed = SqlSchemaMigrationPlanner.Plan(Partial(Declare(3)), Partial(Declare(2)));
        indexed.Operations.Where(operation => operation.Kind is SqlSchemaMigrationOperationKind.DropIndex or SqlSchemaMigrationOperationKind.AddIndex)
            .Select(operation => operation.Kind).ShouldBe([SqlSchemaMigrationOperationKind.DropIndex, SqlSchemaMigrationOperationKind.AddIndex]);
    }

    private readonly record struct Amount(decimal Value);
    private sealed record Row(Amount Value);
}
