using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public sealed class SqlTypeMigrationSafetyTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Schema] - TYPE safety: only proven exact conversions bypass destructive opt-in")]
    [InlineData(DatabaseType.Int16, DatabaseType.Int32, true)]
    [InlineData(DatabaseType.Int64, DatabaseType.Int32, false)]
    [InlineData(DatabaseType.Int32, DatabaseType.Float32, false)]
    [InlineData(DatabaseType.Int32, DatabaseType.Float64, true)]
    [InlineData(DatabaseType.Int64, DatabaseType.Float64, false)]
    [InlineData(DatabaseType.Float32, DatabaseType.Float64, true)]
    [InlineData(DatabaseType.Float64, DatabaseType.Float32, false)]
    public void Plan_TypeConversion_ShouldGatePotentialInformationLoss(DatabaseType source, DatabaseType target, bool safe)
    {
        // Arrange
        var previous = Schema(new("v", source, true), false);
        var desired = Schema(new("v", target, true), false);

        // Act / Assert
        if (safe)
        { SqlSchemaMigrationPlanner.Plan(previous, desired).Operations.Single().Safety.ShouldBe(SqlSchemaMigrationSafety.Safe); }
        else
        { Should.Throw<SqlSchemaMigrationException>(() => SqlSchemaMigrationPlanner.Plan(previous, desired)); }
        SqlSchemaMigrationPlanner.Plan(previous, Schema(new("v", target, true), true)).Operations.Single().Safety
            .ShouldBe(safe ? SqlSchemaMigrationSafety.Safe : SqlSchemaMigrationSafety.Destructive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Schema] - TYPE safety: narrowing and rounding need opt-in even when stored values happen to fit")]
    [InlineData("VARCHAR(8)", "VARCHAR(4)", false)]
    [InlineData("VARCHAR(4)", "VARCHAR(8)", true)]
    [InlineData("NUMERIC(8,3)", "NUMERIC(8,2)", false)]
    [InlineData("NUMERIC(8,2)", "NUMERIC(8,3)", false)]
    [InlineData("NUMERIC(8,2)", "NUMERIC(9,3)", true)]
    [InlineData("NUMERIC(8,2)", "NUMERIC(7,2)", false)]
    public void Plan_FacetConversion_ShouldProveEveryValueFits(string source, string target, bool safe)
    {
        // Arrange
        var type = source.StartsWith("VARCHAR", System.StringComparison.Ordinal) ? DatabaseType.String : DatabaseType.Decimal;
        var previous = Schema(new("v", type, true, CustomType: source), false);
        var desired = Schema(new("v", type, true, CustomType: target), false);

        // Act / Assert
        if (safe)
        { SqlSchemaMigrationPlanner.Plan(previous, desired).Operations.Single().Safety.ShouldBe(SqlSchemaMigrationSafety.Safe); }
        else
        { Should.Throw<SqlSchemaMigrationException>(() => SqlSchemaMigrationPlanner.Plan(previous, desired)); }
    }

    private static SqlCompiledSchema Schema(CompiledSchemaColumn column, bool destructive)
        => new(SqlCompiledSchema.CurrentFormat, "typed", destructive, [],
            [new("items", "row", [column], null, [], [])], []);
}
