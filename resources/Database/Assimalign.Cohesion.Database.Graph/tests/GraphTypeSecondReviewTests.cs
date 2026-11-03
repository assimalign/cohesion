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
}
