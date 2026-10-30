using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public sealed class CompiledPartialIndexTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Schema] - Partial indexes: predicate retyping drops before ALTER and recreates after (#1630)")]
    public void Plan_PredicateColumnTypeChange_ShouldOrderIndexRebuild()
    {
        // Arrange
        var before = Schema("active");
        var oldTable = before.Tables[0];
        var after = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", true, [],
            [new("t", "Tests:Row", [new("k", DatabaseType.Int64, true), new("active", DatabaseType.Boolean, true)], null,
                oldTable.Indexes, [])], []);

        // Act
        var plan = SqlSchemaMigrationPlanner.Plan(before, after);

        // Assert: parser-free schema planning conservatively rebuilds every partial index on a retyped table.
        plan.Operations.Select(operation => operation.Kind).ShouldBe([
            SqlSchemaMigrationOperationKind.DropIndex, SqlSchemaMigrationOperationKind.AlterColumn, SqlSchemaMigrationOperationKind.AddIndex]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Schema] - Partial indexes: v10 persists predicates and migration detects predicate changes (#1630)")]
    public void Serialize_PartialPredicate_ShouldRoundTripAndAffectMigration()
    {
        // Arrange
        var before = Schema("active");
        var after = Schema("active = TRUE");

        // Act
        var restored = SqlCompiledSchemaSerializer.Deserialize(before.CanonicalDocument);
        var plan = SqlSchemaMigrationPlanner.Plan(before, after);

        // Assert
        restored.Tables[0].Indexes[0].Predicate.ShouldBe("active");
        restored.Hash.ShouldBe(before.Hash);
        after.Hash.ShouldNotBe(before.Hash);
        plan.Operations.Count.ShouldBe(2);
        Should.Throw<SqlSchemaValidationException>(() => SqlCompiledSchemaSerializer.Deserialize(
            before.CanonicalDocument.Replace("database-schema/v11", "database-schema/v10", StringComparison.Ordinal)));
    }

    private static SqlCompiledSchema Schema(string predicate) => new(SqlCompiledSchema.CurrentFormat, "app", true, [],
        [new("t", "Tests:Row", [new("k", DatabaseType.Int32, true), new("active", DatabaseType.Boolean, true)], null,
            [new("uq", ["k"], true, predicate)], [])], []);
}
