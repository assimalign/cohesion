using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public sealed class CompiledIndexKeyOrderTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Schema] - Schema v12: omitted and zero-filled orders share identity and plan no changes (#1787)")]
    public void Serialize_DefaultOrders_ShouldShareIdentityAndPlanNoOperations()
    {
        // Arrange
        var omitted = Schema(new("ix", ["a", "b"]));
        var zeros = Schema(new("ix", ["a", "b"], KeyOrders: new CompiledSchemaIndexKeyOrder[2]));

        // Act
        var restored = SqlCompiledSchemaSerializer.Deserialize(omitted.CanonicalDocument);
        var plan = SqlSchemaMigrationPlanner.Plan(restored, zeros);

        // Assert
        default(CompiledSchemaIndexKeyOrder).ShouldBe(CompiledSchemaIndexKeyOrder.AscendingNullsFirst);
        restored.Tables[0].Indexes[0].KeyOrders.ShouldBe([CompiledSchemaIndexKeyOrder.AscendingNullsFirst, CompiledSchemaIndexKeyOrder.AscendingNullsFirst]);
        restored.Hash.ShouldBe(zeros.Hash);
        plan.Operations.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Schema] - Directed indexes: v12 snapshots order, round trips and rebuilds on change (#1631)")]
    public void Serialize_KeyOrders_ShouldRoundTripAndAffectMigration()
    {
        // Arrange
        CompiledSchemaIndexKeyOrder[] orders = [CompiledSchemaIndexKeyOrder.DescendingNullsLast, CompiledSchemaIndexKeyOrder.AscendingNullsFirst];
        var before = Schema(new("ix", ["a", "b"], true, "a > 0", orders));
        orders[0] = CompiledSchemaIndexKeyOrder.AscendingNullsLast;
        var after = Schema(new("ix", ["a", "b"], true, "a > 0", [CompiledSchemaIndexKeyOrder.AscendingNullsLast, CompiledSchemaIndexKeyOrder.AscendingNullsFirst]));

        // Act
        var restored = SqlCompiledSchemaSerializer.Deserialize(before.CanonicalDocument);
        var plan = SqlSchemaMigrationPlanner.Plan(before, after);

        // Assert
        SqlCompiledSchema.CurrentFormat.ShouldBe("cohesion/database-schema/v12");
        restored.Tables[0].Indexes[0].KeyOrders.ShouldBe([CompiledSchemaIndexKeyOrder.DescendingNullsLast, CompiledSchemaIndexKeyOrder.AscendingNullsFirst]);
        restored.Hash.ShouldBe(before.Hash);
        after.Hash.ShouldNotBe(before.Hash);
        plan.Operations.Select(operation => operation.Kind).ShouldBe([SqlSchemaMigrationOperationKind.DropIndex, SqlSchemaMigrationOperationKind.AddIndex]);
        Should.Throw<SqlSchemaValidationException>(() => SqlCompiledSchemaSerializer.Deserialize(
            before.CanonicalDocument.Replace("database-schema/v12", "database-schema/v11", StringComparison.Ordinal)))
            .Errors.ShouldHaveSingleItem().Declaration.ShouldBe("schema.format");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Schema] - Directed indexes: invalid key-order vector is refused (#1631)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Serialize_InvalidOrders_ShouldRefuseAtKeyOrders(bool wrongCount)
    {
        // Arrange
        var index = new CompiledSchemaIndex("ix", ["a", "b"], KeyOrders: wrongCount ? [] : [(CompiledSchemaIndexKeyOrder)4, default]);

        // Act / Assert
        Should.Throw<SqlSchemaValidationException>(() => Schema(index)).Errors.ShouldContain(error => error.Declaration.EndsWith(".keyOrders", StringComparison.Ordinal));
    }

    private static SqlCompiledSchema Schema(CompiledSchemaIndex index) => new(SqlCompiledSchema.CurrentFormat, "app", true, [],
        [new("t", "Tests:Row", [new("a", DatabaseType.Int32, true), new("b", DatabaseType.Int32, true)], null, [index], [])], []);
}
