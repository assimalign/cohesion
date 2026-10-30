using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphSchemaDdlTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: budget refusals leave no rule or discovered tokens")]
    [InlineData(0L, null)]
    [InlineData(null, 0L)]
    [InlineData(null, 1L)]
    public async Task Execute_RefusedBudget_ShouldRollbackEveryTokenAsync(long? work, long? memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl-budget", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var request = GraphQueryRequest.FromGql("CREATE INDEX ix FOR (n:Fresh) ON (n.p)", null,
            new GraphQueryLimits { WorkUnits = work, MemoryBytes = memory });
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory == 1 ? "CG05020" : "CG08021");
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetRelationshipTypesAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetIndexesAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW PROPERTY KEYS")).ShouldBeEmpty();
        (await session.ExecuteAsync("CREATE INDEX ix FOR (n:Fresh) ON (n.p)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: scans and backfill exhaust budgets and roll back all discoveries")]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task Execute_ExhaustedBudget_ShouldRollbackRuleAndAllowRetryAsync(bool constraint, bool relationship, bool backfillMemory, bool backfillWork)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl-budget", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        for (int i = 0; i < 10; i++)
        {
            await session.ExecuteAsync(relationship ? "INSERT (a)-[:N {p:1}]->(a)" : "INSERT (:N {p:1})", cancellationToken: CancellationToken.None);
        }
        var labels = await schema.GetLabelsAsync(CancellationToken.None);
        var types = await schema.GetRelationshipTypesAsync(CancellationToken.None);
        var properties = await GraphCatalogIntrospectionTests.Rows(session, "SHOW PROPERTY KEYS");
        bool populatedIndex = backfillMemory || backfillWork;
        string text = constraint ? "CREATE CONSTRAINT rule FOR " + (relationship ? "()-[n:N]-()" : "(n:N)") + " REQUIRE n.fresh IS :: INTEGER"
            : "CREATE INDEX rule FOR (n:N) ON (n." + (populatedIndex ? "p" : "fresh") + ")";
        var request = GraphQueryRequest.FromGql(text, null, backfillMemory
            ? new GraphQueryLimits { MemoryBytes = 8800 } : new GraphQueryLimits { WorkUnits = backfillWork ? 11 : 1 });
        long postings = relationship ? database.Store.RelationshipTypeEntriesVisited : database.Store.LabelEntriesVisited;
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert: work admission stops the scan; memory admits some backfill entries.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(backfillMemory ? "CG05020" : "CG05004");
        long visited = (relationship ? database.Store.RelationshipTypeEntriesVisited : database.Store.LabelEntriesVisited) - postings;
        visited.ShouldBeGreaterThan(0);
        if (backfillWork) { visited.ShouldBe(10); session.LastStatementMetrics!.Physical.ShouldBe(12); }
        else { visited.ShouldBeLessThan(10); }
        if (backfillMemory)
        {
            session.LastStatementMetrics!.Physical.ShouldBeGreaterThan(1);
            session.LastStatementMetrics.Memory.PeakRetainedBytes.ShouldBeGreaterThan(8192);
        }
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldBe(labels);
        (await schema.GetRelationshipTypesAsync(CancellationToken.None)).ShouldBe(types);
        (await schema.GetIndexesAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldBeEmpty();
        var after = await GraphCatalogIntrospectionTests.Rows(session, "SHOW PROPERTY KEYS");
        after.Count.ShouldBe(properties.Count);
        for (int row = 0; row < after.Count; row++) { after[row].ShouldBe(properties[row]); }
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try { database.Store.HasIndex("N", populatedIndex ? "p" : "fresh", reader.Snapshot).ShouldBeFalse(); }
        finally { await database.Coordinator.RollbackAsync(reader, CancellationToken.None); }
        (await session.ExecuteAsync(text, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        if (constraint) { (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("rule"); }
        else
        {
            (await schema.GetIndexesAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("rule");
            if (populatedIndex) { (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n:N {p:1}) RETURN n.p")).Count.ShouldBe(10); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: named node and relationship constraints discover tokens and drop independently")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_Constraints_ShouldCreateTokensEnforceAndDropAsync(bool relationship)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string pattern = relationship ? "()-[r:Fresh]-()" : "(r:Fresh)";
        var schema = GraphSchema.Open(database, session);
        // Act
        await session.ExecuteAsync("CREATE CONSTRAINT typed FOR " + pattern + " REQUIRE r.p IS :: INTEGER", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE CONSTRAINT required IF NOT EXISTS FOR " + pattern + " REQUIRE r.p IS NOT NULL", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE CONSTRAINT required IF NOT EXISTS FOR " + pattern + " REQUIRE r.p IS NOT NULL", cancellationToken: CancellationToken.None);
        // Assert
        (await schema.GetConstraintsAsync(CancellationToken.None)).Count.ShouldBe(2);
        var shown = await GraphCatalogIntrospectionTests.Rows(session, "SHOW CONSTRAINTS");
        shown.Count.ShouldBe(2);
        shown.Select(row => row[4]).ShouldBe(["required", "typed"]);
        var metadata = (await schema.GetPropertyKeysAsync((await schema.GetConstraintsAsync(CancellationToken.None))[0].DefinitionId, CancellationToken.None)).ShouldHaveSingleItem();
        metadata.ShouldBe(new(metadata.DefinitionId, "p"));
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            relationship ? "INSERT (a)-[:Fresh]->(a)" : "INSERT (:Fresh)", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        await session.ExecuteAsync("DROP CONSTRAINT required", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync(relationship ? "INSERT (a)-[:Fresh]->(a)" : "INSERT (:Fresh)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("DROP CONSTRAINT typed IF EXISTS", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("DROP CONSTRAINT typed IF EXISTS", cancellationToken: CancellationToken.None);
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Schema DDL: generated names are stable across variables and IF NOT EXISTS checks equivalent rules")]
    public async Task Execute_UnnamedRules_ShouldUseStableSemanticNamesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        // Act
        await session.ExecuteAsync("CREATE INDEX FOR (n:New) ON (n.p)", cancellationToken: CancellationToken.None);
        string index = (await schema.GetIndexesAsync(CancellationToken.None)).ShouldHaveSingleItem().Name;
        await session.ExecuteAsync("CREATE INDEX IF NOT EXISTS FOR (other:New) ON (other.p)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("DROP INDEX \"" + index + "\"", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX FOR (x:New) ON (x.p)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE CONSTRAINT FOR (n:New) REQUIRE n.p IS NOT NULL", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE CONSTRAINT alias IF NOT EXISTS FOR (n:New) REQUIRE n.p IS NOT NULL", cancellationToken: CancellationToken.None);
        // Assert
        (await schema.GetIndexesAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe(index);
        index.ShouldStartWith("index_", Case.Sensitive);
        index.Length.ShouldBe(70);
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldStartWith("constraint_", Case.Sensitive);
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW INDEXES")).ShouldHaveSingleItem()[3].ShouldBe(index);
        await session.ExecuteAsync("DROP INDEX missing IF EXISTS", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("DROP CONSTRAINT missing IF EXISTS", cancellationToken: CancellationToken.None);
        var missing = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("DROP INDEX missing", cancellationToken: CancellationToken.None));
        missing.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01021");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: failed token creation leaves no label, type or property metadata")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Execute_FailedCreation_ShouldRollbackEveryTokenAsync(int form)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        string huge = new('x', 8192);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (form == 0) { await schema.CreateIndexAsync("Fresh", huge, "p", CancellationToken.None); }
            else { await session.ExecuteAsync(form == 1 ? "CREATE INDEX \"" + huge + "\" FOR (n:Fresh) ON (n.p)"
                : "CREATE CONSTRAINT \"" + huge + "\" FOR " + (form == 2 ? "(n:Fresh)" : "()-[n:Fresh]-()") + " REQUIRE n.p IS NOT NULL", cancellationToken: CancellationToken.None); }
        });
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02009");
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetRelationshipTypesAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetIndexesAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await GraphCatalogIntrospectionTests.Rows(session, "SHOW PROPERTY KEYS")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: duplicate rules retain structured diagnostics and discover no new owner")]
    [InlineData("CREATE INDEX shared FOR (n:Fresh) ON (n.p)", "CG03008")]
    [InlineData("CREATE CONSTRAINT shared FOR (n:Fresh) REQUIRE n.p IS NOT NULL", "CG03008")]
    [InlineData("CREATE CONSTRAINT shared IF NOT EXISTS FOR (n:Fresh) REQUIRE n.p IS NOT NULL", "CG03008")]
    [InlineData("CREATE INDEX other FOR (n:Known) ON (n.p)", "CG03003")]
    [InlineData("CREATE CONSTRAINT other FOR (n:Known) REQUIRE n.q IS :: STRING", "CG03009")]
    public async Task Execute_DuplicateRule_ShouldRefuseWithoutNewTokensAsync(string query, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await session.ExecuteAsync("CREATE INDEX shared FOR (n:Known) ON (n.p)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE CONSTRAINT existing FOR (n:Known) REQUIRE n.q IS :: STRING", cancellationToken: CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("Known");
        (await schema.GetIndexesAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("shared");
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("existing");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: typed and text schema writes refuse and abort explicit transactions")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task Execute_ExplicitTransaction_ShouldRefuseEverySchemaWriteAsync(int form)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Earlier)", cancellationToken: CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            switch (form)
            {
                case 0: await schema.SaveLabelAsync(new(Guid.NewGuid(), "Fresh"), CancellationToken.None); break;
                case 1: await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), "Fresh"), CancellationToken.None); break;
                case 2: await schema.SavePropertyKeyAsync(new(id, "p"), CancellationToken.None); break;
                case 3: await schema.CreateIndexAsync("Fresh", "ix", "p", CancellationToken.None); break;
                case 4: await schema.CreateConstraintAsync(new(id, "c", "p"), CancellationToken.None); break;
                case 5: await schema.DropLabelAsync("N", CancellationToken.None); break;
                case 6: await schema.DropRelationshipTypeAsync("N", CancellationToken.None); break;
                case 7: await schema.DropIndexAsync("missing", CancellationToken.None); break;
                case 8: await schema.DropConstraintAsync("missing", CancellationToken.None); break;
                default: await session.ExecuteAsync("CREATE INDEX IF NOT EXISTS FOR (n:Fresh) ON (n.p)", cancellationToken: CancellationToken.None); break;
            }
        });
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04025");
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync(CancellationToken.None);
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("N");
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n) RETURN n")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: metadata validation precedes statement admission")]
    [InlineData(false)] [InlineData(true)]
    public async Task SaveDefinition_InvalidArgument_ShouldLeaveTransactionActiveAsync(bool relationship)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        // Act
        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            if (relationship) { await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), " "), CancellationToken.None); }
            else { await schema.SaveLabelAsync(new(Guid.NewGuid(), " "), CancellationToken.None); }
        });
        // Assert
        await session.ExecuteAsync("INSERT (:Kept)", cancellationToken: CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("Kept");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema DDL: property saves and empty drops use only owner postings")]
    [InlineData(false)] [InlineData(true)]
    public async Task SavePropertyAndDrop_UnrelatedData_ShouldVisitNoUnrelatedRecordsAsync(bool relationship)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        if (relationship) { await schema.SaveRelationshipTypeAsync(new(id, "Empty"), CancellationToken.None); }
        else { await schema.SaveLabelAsync(new(id, "Empty"), CancellationToken.None); }
        for (int i = 0; i < 32; i++) { await session.ExecuteAsync("INSERT (a:Other)-[:Other]->(a)", cancellationToken: CancellationToken.None); }
        long nodes = database.Store.NodeRecordsDecoded, labels = database.Store.LabelEntriesVisited, types = database.Store.RelationshipTypeEntriesVisited;
        // Act
        await schema.SavePropertyKeyAsync(new(id, "p", DatabaseType.Int64, true), CancellationToken.None);
        if (relationship) { await schema.DropRelationshipTypeAsync("Empty", CancellationToken.None); }
        else { await schema.DropLabelAsync("Empty", CancellationToken.None); }
        // Assert
        (database.Store.NodeRecordsDecoded - nodes).ShouldBe(0);
        (database.Store.LabelEntriesVisited - labels).ShouldBe(0);
        (database.Store.RelationshipTypeEntriesVisited - types).ShouldBe(0);
    }
}
