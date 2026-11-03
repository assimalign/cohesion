using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTypeSecondReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - ALTER tightening scans existing members and preserves the prior declaration (#1806)")]
    [InlineData("implied")]
    [InlineData("required")]
    [InlineData("type")]
    [InlineData("property")]
    public async Task AlterGraphTypeAsync_TighteningExistingType_ShouldValidateMembersAsync(string change)
    {
        // Arrange: exactly one existing member violates the proposed tightening.
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N", properties:
            [GraphPropertyType.Create("p", DatabaseType.Int64)])]), CancellationToken.None);
        var values = new Dictionary<string, object?>();
        if (change != "required") { values["p"] = 1L; }
        if (change == "property") { values["q"] = "x"; }
        await database.CreateNodeAsync(session, ["N"], values, CancellationToken.None);
        var proposed = GraphNodeType.Create("N", change == "implied" ? ["I"] : [],
            change == "property" ? [GraphPropertyType.Create("p", DatabaseType.Int64), GraphPropertyType.Create("q", DatabaseType.Int64)] :
            [GraphPropertyType.Create("p", change == "type" ? DatabaseType.String : DatabaseType.Int64, change == "required")]);
        long decoded = database.Store.NodeRecordsDecoded;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await schema.AlterGraphTypeAsync(GraphType.Create([proposed]), CancellationToken.None));

        // Assert: the scan ran and a failed publication retained the complete old rule.
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03010");
        database.Store.NodeRecordsDecoded.ShouldBeGreaterThan(decoded);
        var previous = (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.ShouldHaveSingleItem();
        previous.IdentifyingLabel.ShouldBe("N");
        previous.ImpliedLabels.ShouldBeEmpty();
        var property = previous.Properties.ShouldHaveSingleItem();
        (property.Name, property.Type, property.Required).ShouldBe(("p", DatabaseType.Int64, false));
        await database.CreateNodeAsync(session, ["N"], cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - adopted definitions lock changed and removed rules while unrelated ADD proceeds (#1806)")]
    [InlineData("drop")]
    [InlineData("dropAll")]
    [InlineData("set")]
    [InlineData("alter")]
    [InlineData("implied")]
    [InlineData("edge")]
    public async Task ChangeGraphTypeAsync_AdoptedDefinition_ShouldLockOnlyChangesAsync(string change)
    {
        // Arrange: publish ad hoc, then adopt an existing definition into a schema.
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Customer", change == "implied" ? ["Owned"] : [],
            [GraphPropertyType.Create("email", DatabaseType.String, true)])],
            change == "edge" ? [GraphEdgeType.Create("R", "Customer", "Customer")] : []), CancellationToken.None);
        string owned = change == "implied" ? "Owned" : "Customer";
        if (change == "edge")
        {
            var relationship = (await schema.GetRelationshipTypesAsync(CancellationToken.None)).ShouldHaveSingleItem();
            await schema.SaveRelationshipTypeAsync(new(relationship.Id, "R", DatabaseObjectOwner.Schema, "Application"), CancellationToken.None);
            owned = "R";
        }
        else
        {
            var label = (await schema.GetLabelsAsync(CancellationToken.None)).Single(item => item.Name == owned);
            await schema.SaveLabelAsync(new(label.Id, owned, DatabaseObjectOwner.Schema, "Application"), CancellationToken.None);
        }

        // Act: an unrelated change proceeds despite the adopted definition.
        await schema.AddGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Invoice")]), CancellationToken.None);
        var previous = (await schema.GetGraphTypeAsync(CancellationToken.None))!;
        previous.NodeTypes.Select(item => item.IdentifyingLabel).ShouldBe(["Customer", "Invoice"]);
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
        {
            switch (change)
            {
                case "drop": await schema.DropGraphTypeAsync(["Customer"], [], CancellationToken.None); break;
                case "dropAll": await schema.DropGraphTypeAsync(CancellationToken.None); break;
                case "set": await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Invoice")]), CancellationToken.None); break;
                case "edge": await schema.DropGraphTypeAsync([], ["R"], CancellationToken.None); break;
                default: await schema.AlterGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Customer")]), CancellationToken.None); break;
            }
        });

        // Assert: removals, including a removed implied label, cannot bypass schema ownership.
        error.ObjectName.ShouldBe(owned);
        AssertDeclarations((await schema.GetGraphTypeAsync(CancellationToken.None))!, previous);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - unrelated ADD decodes no keys or constraints of 500 retained node types (#1806)")]
    public async Task AddGraphTypeAsync_AnnotatedRetainedTypes_ShouldAvoidMetadataDecodesAsync()
    {
        // Arrange / Act: identical type/token catalogs differ only by kind-3/5 annotations.
        long plain = await CountReadsAsync(false);
        long annotated = await CountReadsAsync(true);

        // Assert: none of the extra physical key/constraint records were decoded.
        plain.ShouldBeGreaterThan(500);
        annotated.ShouldBe(plain);

        static async Task<long> CountReadsAsync(bool annotate)
        {
            await using var engine = GraphDatabaseEngine.Create("types", new() { MaintenanceInterval = TimeSpan.FromHours(1) });
            var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            var schema = GraphSchema.Open(database, session);
            var nodes = Enumerable.Range(0, 500).Select(i => GraphNodeType.Create("N" + i, properties:
                [GraphPropertyType.Create("p", DatabaseType.Int64)])).ToArray();
            await schema.SetGraphTypeAsync(GraphType.Create(nodes), CancellationToken.None);
            if (annotate)
            {
                foreach (var label in await schema.GetLabelsAsync(CancellationToken.None))
                {
                    await schema.SavePropertyKeyAsync(new(label.Id, "p", DatabaseType.Int64), CancellationToken.None);
                    await schema.CreateConstraintAsync(new(label.Id, "c_" + label.Name, "p", DatabaseType.Int64), CancellationToken.None);
                }
            }
            long reads = 0;
            database.Catalog.BeforeRecordRead = () => reads++;
            try { await schema.AddGraphTypeAsync(GraphType.Create([GraphNodeType.Create("Extra")]), CancellationToken.None); }
            finally { database.Catalog.BeforeRecordRead = null; }
            (await schema.GetGraphTypeAsync(CancellationToken.None))!.NodeTypes.Count.ShouldBe(501);
            return reads;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - compatibility comparisons consume statement work in both publication orders (#1806)")]
    [InlineData("type")]
    [InlineData("key")]
    [InlineData("constraint")]
    public async Task ChangeGraphTypeAsync_CompatibilityKeys_ShouldChargeWorkAsync(string change)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None);
        var properties = Enumerable.Range(0, 30).Select(i => GraphPropertyType.Create("p" + i, DatabaseType.Int64)).ToArray();
        if (change == "type")
        {
            foreach (var property in properties) { await schema.SavePropertyKeyAsync(new(id, property.Name, property.Type), CancellationToken.None); }
        }
        else { await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N", properties: properties)]), CancellationToken.None); }
        engine.EngineOptions.QueryWorkBudget = 20;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (change == "type") { await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N", properties: [GraphPropertyType.Create("z", DatabaseType.Int64)])]), CancellationToken.None); }
            else if (change == "key") { await schema.SavePropertyKeyAsync(new(id, "z", DatabaseType.Int64), CancellationToken.None); }
            else { await schema.CreateConstraintAsync(new(id, "c", "z", DatabaseType.Int64), CancellationToken.None); }
        });

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        (await schema.GetPropertyKeysAsync(id, CancellationToken.None)).ShouldNotContain(item => item.Name == "z");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - reverse compatibility admits the kind-7 decode grant before decoding (#1806)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavePropertyKeyAsync_GraphTypeDecode_ShouldChargeMemoryAsync(bool constraint)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"), CancellationToken.None);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N", properties:
            Enumerable.Range(0, 30).Select(i => GraphPropertyType.Create("property_" + i, DatabaseType.Int64)))]), CancellationToken.None);
        engine.EngineOptions.QueryMemoryBudget = 1024;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (constraint) { await schema.CreateConstraintAsync(new(id, "c", "property_0", DatabaseType.Int64), CancellationToken.None); }
            else { await schema.SavePropertyKeyAsync(new(id, "property_0", DatabaseType.Int64), CancellationToken.None); }
        });

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - token DROP uses the identifying kind-7 or relationship kind-8 key (#1806)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropDefinitionAsync_LargeUnrelatedType_ShouldUseKeyedLookupAsync(bool relationship)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("types", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), "Loose"), CancellationToken.None);
        await schema.SetGraphTypeAsync(GraphType.Create(Enumerable.Range(0, 100).Select(i => GraphNodeType.Create("N" + i)),
            Enumerable.Range(0, 100).Select(i => GraphEdgeType.Create("R" + i, "N0", "N0"))), CancellationToken.None);
        engine.EngineOptions.QueryWorkBudget = 20;

        engine.EngineOptions.QueryMemoryBudget = 8192;

        // Act / Assert
        if (relationship)
        {
            await schema.DropRelationshipTypeAsync("Loose", CancellationToken.None);
            (await schema.GetRelationshipTypesAsync(CancellationToken.None)).ShouldNotContain(item => item.Name == "Loose");
        }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.DropLabelAsync("N0", CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - element factories validate names before copying properties (#1806)")]
    [InlineData("identifyingLabel")]
    [InlineData("impliedLabels")]
    [InlineData("relationshipType")]
    [InlineData("sourceNodeType")]
    [InlineData("targetNodeType")]
    public void Create_InvalidNameAndProperty_ShouldReportNameFirst(string parameter)
    {
        // Arrange / Act
        var error = Should.Throw<ArgumentException>(() =>
        {
            if (parameter == "identifyingLabel") { GraphNodeType.Create(" ", properties: [null!]); }
            else if (parameter == "impliedLabels") { GraphNodeType.Create("N", [" "], [null!]); }
            else { GraphEdgeType.Create(parameter == "relationshipType" ? " " : "R", parameter == "sourceNodeType" ? " " : "N",
                parameter == "targetNodeType" ? " " : "N", [null!]); }
        });

        // Assert
        error.ParamName.ShouldBe(parameter);
    }

    internal static void AssertDeclarations(GraphType actual, GraphType expected)
    {
        actual.NodeTypes.Select(item => item.IdentifyingLabel).ShouldBe(expected.NodeTypes.Select(item => item.IdentifyingLabel));
        foreach (var node in expected.NodeTypes)
        {
            var found = actual.NodeTypes.Single(item => item.IdentifyingLabel == node.IdentifyingLabel);
            found.ImpliedLabels.ShouldBe(node.ImpliedLabels);
            found.Properties.Select(item => (item.Name, item.Type, item.Required)).ShouldBe(node.Properties.Select(item => (item.Name, item.Type, item.Required)));
        }
        actual.EdgeTypes.Select(item => (item.RelationshipType, item.SourceNodeType, item.TargetNodeType))
            .ShouldBe(expected.EdgeTypes.Select(item => (item.RelationshipType, item.SourceNodeType, item.TargetNodeType)));
        foreach (var edge in expected.EdgeTypes)
        {
            actual.EdgeTypes.Single(item => item.RelationshipType == edge.RelationshipType).Properties.Select(item => (item.Name, item.Type, item.Required))
                .ShouldBe(edge.Properties.Select(item => (item.Name, item.Type, item.Required)));
        }
    }
}
