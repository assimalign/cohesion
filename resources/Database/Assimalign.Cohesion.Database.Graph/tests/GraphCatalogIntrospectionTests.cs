using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCatalogIntrospectionTests
{
    [Fact]
    public async Task ShowExposesCatalogDefinitionsWithoutRequiringGraphData()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("catalog");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        Guid label = Guid.NewGuid();
        Guid type = Guid.NewGuid();
        await schema.SaveLabelAsync(new(label, "Person"));
        await schema.SaveRelationshipTypeAsync(new(type, "Person"));
        await schema.SavePropertyKeyAsync(new(label, "name", DatabaseType.String, true));
        await schema.SavePropertyKeyAsync(new(type, "since"));
        await schema.CreateIndexAsync("Person", "by_name", "name");

        var labels = await Rows(session, "show labels;");
        labels.Single().ShouldBe(new object?[] { "catalog", label, "Person" });
        (await Rows(session, "SHOW RELATIONSHIP TYPES")).Single().ShouldBe(new object?[] { "catalog", type, "Person" });
        var properties = await Rows(session, "SHOW PROPERTY KEYS");
        properties.Count.ShouldBe(2);
        properties[0].ShouldBe(new object?[] { "catalog", "LABEL", label, "Person", "name", "String", true });
        properties[1].ShouldBe(new object?[] { "catalog", "RELATIONSHIP TYPE", type, "Person", "since", null, false });
        (await Rows(session, "SHOW INDEXES")).Single().ShouldBe(new object?[] { "catalog", label, "Person", "by_name", "name", false });
        (await Rows(session, "MATCH (n) RETURN n")).ShouldBeEmpty();

        await using var result = (QueryResultSet)await session.ExecuteAsync("SHOW PROPERTY KEYS");
        result.Columns.Select(column => column.Name).ShouldBe([
            "DATABASE_NAME", "DEFINITION_TYPE", "DEFINITION_ID", "DEFINITION_NAME", "PROPERTY_KEY", "DATA_TYPE", "IS_REQUIRED"]);
        result.Columns.Select(column => column.Type).ShouldBe([
            DatabaseType.String, DatabaseType.String, DatabaseType.Guid, DatabaseType.String, DatabaseType.String, DatabaseType.String, DatabaseType.Boolean]);
        result.Columns.Select(column => column.IsNullable).ShouldBe([false, false, false, false, false, true, false]);
    }

    [Fact]
    public async Task OwnershipReportsActualDefinitionAuthorityAndChildEnforcement()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("ownership");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        Guid managed = Guid.NewGuid();
        await schema.SaveLabelAsync(new(managed, "Managed"));
        await schema.SavePropertyKeyAsync(new(managed, "name", DatabaseType.String));
        await schema.CreateIndexAsync("Managed", "by_name", "name");
        await schema.SaveLabelAsync(new(managed, "Managed", DatabaseObjectOwner.Schema, "Application"));
        await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), "MANAGED_TYPE", DatabaseObjectOwner.Schema, "Application"));
        await schema.SaveLabelAsync(new(Guid.NewGuid(), "Adhoc"));

        var rows = await Rows(session, "SHOW OBJECT OWNERSHIP");
        rows.Count.ShouldBe(5);
        var adhoc = rows.Single(row => Equals(row[5], "Adhoc"));
        adhoc[6].ShouldBe("Adhoc");
        adhoc[7].ShouldBeNull();
        await using var ownership = (QueryResultSet)await session.ExecuteAsync("SHOW OBJECT OWNERSHIP");
        ownership.Columns[7].IsNullable.ShouldBeTrue();
        var managedRows = rows.Where(row => Equals(row[3], "Managed")).ToArray();
        managedRows.Select(row => row[4]).ShouldBe(["LABEL", "PROPERTY KEY", "INDEX"]);
        foreach (var row in rows.Where(row => !ReferenceEquals(row, adhoc)))
        {
            row[6].ShouldBe("Schema");
            row[7].ShouldBe("Application");
        }
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.DropLabelAsync("Managed"));
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.SavePropertyKeyAsync(new(managed, "other")));
    }

    [Theory]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task CatalogReadsUseSessionVisibilityAndFreshStatementsObserveCommittedChanges(IsolationLevel isolation)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("visibility");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, writer);
        await using var transaction = await reader.BeginTransactionAsync(isolation);
        (await Rows(reader, "SHOW LABELS")).ShouldBeEmpty();
        Guid label = Guid.NewGuid();
        await schema.SaveLabelAsync(new(label, "Fresh"));
        await schema.SavePropertyKeyAsync(new(label, "value", DatabaseType.Int64));
        await schema.CreateIndexAsync("Fresh", "by_value", "value");
        (await Rows(reader, "SHOW LABELS")).Count.ShouldBe(isolation == IsolationLevel.Snapshot ? 0 : 1);
        (await Rows(reader, "SHOW PROPERTY KEYS")).Count.ShouldBe(isolation == IsolationLevel.Snapshot ? 0 : 1);
        (await Rows(reader, "SHOW INDEXES")).Count.ShouldBe(isolation == IsolationLevel.Snapshot ? 0 : 1);
        await transaction.CommitAsync();
        (await Rows(reader, "SHOW LABELS")).Count.ShouldBe(1);
        await schema.DropLabelAsync("Fresh");
        foreach (string subject in Subjects)
        {
            (await Rows(reader, "SHOW " + subject)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task UncommittedDefinitionsStayPrivateAndRollbackLeavesNoMaterializedCatalogRows()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("rollback");
        await using var writer = await database.CreateSessionAsync();
        await using var reader = await database.CreateSessionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await writer.ExecuteAsync("INSERT (:Transient {value: 1})");
        (await Rows(writer, "SHOW LABELS")).Count.ShouldBe(1);
        (await Rows(writer, "SHOW PROPERTY KEYS")).Count.ShouldBe(1);
        (await Rows(reader, "SHOW LABELS")).ShouldBeEmpty();
        await transaction.RollbackAsync();
        foreach (string subject in Subjects) { (await Rows(writer, "SHOW " + subject)).ShouldBeEmpty(); }
    }

    [Theory]
    [InlineData("LABELS", "DELETE n")]
    [InlineData("RELATIONSHIP TYPES", "DETACH DELETE r")]
    [InlineData("PROPERTY KEYS", "SET key.name = 'changed'")]
    [InlineData("INDEXES", "DROP INDEX by_name")]
    [InlineData("OBJECT OWNERSHIP", "INSERT (:Injected)")]
    public async Task CatalogMutationFailsWithStableDiagnosticAndNoEffects(string subject, string mutation)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("readonly");
        await using var session = await database.CreateSessionAsync();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"SHOW {subject} {mutation}"));
        error.Message.ShouldContain("GQL0007: Graph catalog introspection is read-only.");
        (await Rows(session, "SHOW LABELS")).ShouldBeEmpty();

        var expression = new GqlQueryExpression([], null,
            [new GqlPathPattern([new GqlNodePattern("n", ["Injected"], new Dictionary<string, object?>())], [])], [], false, [])
            { CatalogSurface = GqlCatalogSurface.Labels };
        var direct = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(expression))));
        direct.Message.ShouldBe("GQL0007: Graph catalog introspection is read-only.");
        (await Rows(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    internal static readonly string[] Subjects = ["LABELS", "RELATIONSHIP TYPES", "PROPERTY KEYS", "INDEXES", "OBJECT OWNERSHIP"];

    internal static async Task<List<object?[]>> Rows(IDatabaseSession session, string command)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(command);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync())
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
