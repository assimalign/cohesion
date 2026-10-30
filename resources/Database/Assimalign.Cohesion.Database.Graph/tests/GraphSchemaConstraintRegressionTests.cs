using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphSchemaConstraintRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: incompatible metadata reports CG03009 with or without owner data")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavePropertyKeyAsync_IncompatibleNamedType_ShouldReportStructuredConflictAsync(bool relationship, bool populated)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        if (relationship) { await schema.SaveRelationshipTypeAsync(new(id, "T"), CancellationToken.None); }
        else { await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None); }
        await schema.CreateConstraintAsync(new(id, "typed", "p", DatabaseType.Int64), CancellationToken.None);
        if (populated)
        {
            await session.ExecuteAsync(relationship ? "INSERT (a)-[:T {p:1}]->(a)" : "INSERT (:N {p:1})",
                cancellationToken: CancellationToken.None);
        }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await schema.SavePropertyKeyAsync(new(id, "p", DatabaseType.String), CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03009");
        (await schema.FindConstraintAsync("typed", CancellationToken.None)).ShouldNotBeNull().Type.ShouldBe(DatabaseType.Int64);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: property metadata round trips preserve independent named-rule lifetime")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GetPropertyKeysAsync_NamedRuleRoundTrip_ShouldLeaveOnlyDeclaredMetadataAfterDropAsync(bool relationship, bool existence)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        if (relationship) { await schema.SaveRelationshipTypeAsync(new(id, "T"), CancellationToken.None); }
        else { await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None); }
        await schema.CreateConstraintAsync(new(id, "rule", "p", existence ? null : DatabaseType.Int64), CancellationToken.None);
        var metadata = (await schema.GetPropertyKeysAsync(id, CancellationToken.None)).ShouldHaveSingleItem();
        var shown = (await GraphCatalogIntrospectionTests.Rows(session, "SHOW PROPERTY KEYS")).ShouldHaveSingleItem();
        shown[5].ShouldBe(existence ? null : "Int64");
        shown[6].ShouldBe(existence);

        // Act
        await schema.SavePropertyKeyAsync(metadata, CancellationToken.None);
        await schema.DropConstraintAsync("rule", CancellationToken.None);

        // Assert
        (await schema.GetPropertyKeysAsync(id, CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(new(id, "p"));
        await session.ExecuteAsync(relationship ? "INSERT (a)-[:T]->(a), (b)-[:T {p:'free'}]->(b)"
            : "INSERT (:N), (:N {p:'free'})", cancellationToken: CancellationToken.None);
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: invalid arguments leave an explicit transaction usable")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task CreateConstraintAsync_InvalidArgument_ShouldPreserveExplicitTransactionAsync(int invalid)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None);
        var constraint = invalid switch
        {
            0 => new GraphConstraintMetadata(id, null!, "p"),
            1 => new(id, " ", "p"),
            2 => new(id, "rule", null!),
            3 => new(id, "rule", " "),
            4 => new(Guid.Empty, "rule", "p"),
            5 => new(id, "rule", "p", DatabaseType.Guid),
            6 => new(id, "rule", "p", (DatabaseType)254),
            _ => default,
        };
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {p:1})", cancellationToken: CancellationToken.None);

        // Act
        if (invalid is 5 or 6)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await schema.CreateConstraintAsync(constraint, CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        }
        else
        {
            await Should.ThrowAsync<ArgumentException>(async () =>
                await schema.CreateConstraintAsync(constraint, CancellationToken.None));
        }

        // Assert: a successful write and commit also retain the work preceding the bad argument.
        await schema.CreateConstraintAsync(new(id, "valid", "p"), CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {p:2})", cancellationToken: CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n:N) RETURN n.p ORDER BY n.p")).Select(row => row[0]).ShouldBe([1L, 2L]);
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldHaveSingleItem().Name.ShouldBe("valid");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Constraints: an empty relationship type visits no nodes in a populated graph")]
    public async Task CreateConstraintAsync_EmptyRelationshipType_ShouldVisitNoNodesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        await schema.SaveRelationshipTypeAsync(new(id, "Empty"), CancellationToken.None);
        for (int i = 0; i < 32; i++) { await database.CreateNodeAsync(session, ["Other"], cancellationToken: CancellationToken.None); }
        long decoded = database.Store.NodeRecordsDecoded, postings = database.Store.RelationshipTypeEntriesVisited;

        // Act
        await schema.CreateConstraintAsync(new(id, "rule", "p"), CancellationToken.None);

        // Assert
        (database.Store.NodeRecordsDecoded - decoded).ShouldBe(0);
        (database.Store.RelationshipTypeEntriesVisited - postings).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: an identical unnamed declaration survives a named rule and its drop")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateConstraintAsync_IdenticalUnnamedRule_ShouldRetainIndependentDeclarationAsync(bool relationship, bool existence)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        if (relationship) { await schema.SaveRelationshipTypeAsync(new(id, "T"), CancellationToken.None); }
        else { await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None); }
        var declaration = new GraphPropertyKeyMetadata(id, "p", existence ? null : DatabaseType.Int64, existence);
        await schema.SavePropertyKeyAsync(declaration, CancellationToken.None);

        // Act
        await schema.CreateConstraintAsync(new(id, "rule", "p", existence ? null : DatabaseType.Int64), CancellationToken.None);
        await schema.DropConstraintAsync("rule", CancellationToken.None);

        // Assert
        (await schema.GetPropertyKeysAsync(id, CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(declaration);
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldBeEmpty();
        string invalid = existence ? "" : " {p:'bad'}";
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            relationship ? "INSERT (a)-[:T" + invalid + "]->(a)" : "INSERT (:N" + invalid + ")",
            cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: oversized metadata uses the documented DatabaseException and CG02009")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateConstraintAsync_OversizedMetadata_ShouldReportDatabaseExceptionAsync(bool propertyKey)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None);
        string oversized = new('x', 8192);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await schema.CreateConstraintAsync(
            new(id, propertyKey ? "rule" : oversized, propertyKey ? oversized : "p"), CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02009");
        (await schema.GetConstraintsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await schema.GetPropertyKeysAsync(id, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: owner postings bound validation and stop at the first violation")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateConstraintAsync_OwnerPostings_ShouldStreamOnlyMembersAsync(bool relationship, bool invalidFirst)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        if (relationship) { await schema.SaveRelationshipTypeAsync(new(id, "T"), CancellationToken.None); }
        else { await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None); }
        for (int i = 0; i < 16; i++)
        {
            await session.ExecuteAsync("INSERT (a:Other)-[:Unrelated]->(a)", cancellationToken: CancellationToken.None);
        }
        for (int i = 0; i < 4; i++)
        {
            string properties = invalidFirst && i == 0 ? "" : " {p:1}";
            await session.ExecuteAsync(relationship ? "INSERT (a)-[:T" + properties + "]->(a)" : "INSERT (:N" + properties + ")",
                cancellationToken: CancellationToken.None);
        }
        long decoded = database.Store.NodeRecordsDecoded, labels = database.Store.LabelEntriesVisited;
        long types = database.Store.RelationshipTypeEntriesVisited;

        // Act
        if (invalidFirst)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await schema.CreateConstraintAsync(new(id, "rule", "p"), CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03010");
        }
        else { await schema.CreateConstraintAsync(new(id, "rule", "p"), CancellationToken.None); }

        // Assert: early refusal reads one entity, proving validation does not first materialize its owner.
        long expected = invalidFirst ? 1 : 4;
        (database.Store.NodeRecordsDecoded - decoded).ShouldBe(relationship ? 0 : expected);
        // The label stream stages one next posting before yielding the current node.
        (database.Store.LabelEntriesVisited - labels).ShouldBe(relationship ? 0 : invalidFirst ? 2 : expected);
        (database.Store.RelationshipTypeEntriesVisited - types).ShouldBe(relationship ? expected : 0);
    }
}
