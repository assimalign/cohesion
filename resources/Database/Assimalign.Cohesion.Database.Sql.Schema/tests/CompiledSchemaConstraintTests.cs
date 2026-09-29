using System;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public sealed class CompiledSchemaConstraintTests
{
    [Fact]
    public void ForeignKeys_ShouldBePlannedAfterEveryTableAndUniqueIndex()
    {
        SqlCompiledSchema schema = Schema(
            Table("a_child", [Reference("fk_child", "z_parent")]),
            Table("z_parent", [Reference("fk_parent", "a_child")], [new CompiledSchemaIndex("uq_parent", ["id"], true)]));

        SqlSchemaMigrationPlan plan = SqlSchemaMigrationPlanner.Plan(null, schema);

        plan.Operations.Select(operation => operation.Kind).ShouldBe([
            SqlSchemaMigrationOperationKind.AddTable,
            SqlSchemaMigrationOperationKind.AddTable,
            SqlSchemaMigrationOperationKind.AddIndex,
            SqlSchemaMigrationOperationKind.AddConstraint,
            SqlSchemaMigrationOperationKind.AddConstraint]);
        plan.Operations.Where(operation => operation.Kind == SqlSchemaMigrationOperationKind.AddTable)
            .ShouldAllBe(operation => operation.Table!.Constraints.Count == 0);
        plan.Operations[^2].Constraint!.ReferencedObject.ShouldBe("z_parent");
    }

    [Fact]
    public void ConstraintReplacement_ShouldDropOldDefinitionBeforeAddingNewDefinition()
    {
        SqlCompiledSchema before = Schema(Table("child", [Reference("fk", "parent")]), Table("parent", []));
        SqlCompiledSchema after = Schema(Table("child", [Reference("fk", "parent") with { OnDelete = CompiledSchemaReferentialAction.Cascade }]), Table("parent", []));

        SqlSchemaMigrationPlan plan = SqlSchemaMigrationPlanner.Plan(before, after);

        plan.Operations.Select(operation => operation.Kind).ShouldBe([
            SqlSchemaMigrationOperationKind.DropConstraint, SqlSchemaMigrationOperationKind.AddConstraint]);
        plan.Operations[0].Constraint!.OnDelete.ShouldBe(CompiledSchemaReferentialAction.Restrict);
        plan.Operations[1].Constraint!.OnDelete.ShouldBe(CompiledSchemaReferentialAction.Cascade);
    }

    [Fact]
    public void ReferentialAction_ShouldRoundTripWithoutChangingLegacyRestrictDocuments()
    {
        SqlCompiledSchema restrict = Schema(Table("child", [Reference("fk", "parent")]), Table("parent", []));
        string legacy = restrict.CanonicalDocument;
        legacy.ShouldNotContain("onDelete");
        SqlCompiledSchemaSerializer.Deserialize(legacy).Hash.ShouldBe(restrict.Hash);

        SqlCompiledSchema cascade = Schema(Table("child", [Reference("fk", "parent") with { OnDelete = CompiledSchemaReferentialAction.Cascade }]), Table("parent", []));
        cascade.CanonicalDocument.ShouldContain("\"onDelete\":1");
        SqlCompiledSchemaSerializer.Deserialize(cascade.CanonicalDocument).Tables[0].Constraints[0].OnDelete
            .ShouldBe(CompiledSchemaReferentialAction.Cascade);
    }

    [Fact]
    public void TableCheck_ShouldAcceptSqlExpressionWithoutAdvisoryColumnList()
    {
        var check = new CompiledSchemaConstraint("ck", CompiledSchemaConstraintKind.Check,
            [], null, [], new CompiledSchemaExpression("id > 0"));
        SqlCompiledSchema schema = Schema(Table("t", [check]));
        SqlCompiledSchemaSerializer.Deserialize(schema.CanonicalDocument).Tables[0].Constraints[0].Expression!.CanonicalText.ShouldBe("id > 0");
        SqlSchemaMigrationPlanner.Plan(null, schema).Operations[^1].Kind.ShouldBe(SqlSchemaMigrationOperationKind.AddConstraint);
    }

    [Fact]
    public void InvalidReferentialAction_ShouldBeRejected()
    {
        Should.Throw<SqlSchemaValidationException>(() => Schema(
            Table("child", [Reference("fk", "parent") with { OnDelete = (CompiledSchemaReferentialAction)99 }]), Table("parent", [])))
            .Errors.ShouldContain(error => error.Declaration.EndsWith("onDelete", StringComparison.Ordinal));
    }

    private static CompiledSchemaConstraint Reference(string name, string parent)
        => new(name, CompiledSchemaConstraintKind.Reference, ["id"], parent, ["id"]);

    private static CompiledSchemaTable Table(string name, CompiledSchemaConstraint[] constraints, CompiledSchemaIndex[]? indexes = null)
        => new(name, $"Example.{name}", [new CompiledSchemaColumn("id", DatabaseType.Int32, false)],
            new CompiledSchemaKey($"pk_{name}", ["id"]), indexes ?? [], constraints);

    private static SqlCompiledSchema Schema(params CompiledSchemaTable[] tables)
        => new(SqlCompiledSchema.CurrentFormat, "constraints", EngineModel.Sql, false, [], tables, [], [], [], []);
}
