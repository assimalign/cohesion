using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentOrderingTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - NULLS moves the block with Couchbase direction inside it (#1730)")]
    [InlineData("ASC", "m,n,f,t,l,h,s,a,o")]
    [InlineData("DESC", "o,a,s,h,l,t,f,n,m")]
    [InlineData("ASC NULLS FIRST", "m,n,f,t,l,h,s,a,o")]
    [InlineData("ASC NULLS LAST", "f,t,l,h,s,a,o,m,n")]
    [InlineData("DESC NULLS FIRST", "n,m,o,a,s,h,l,t,f")]
    [InlineData("DESC NULLS LAST", "o,a,s,h,l,t,f,n,m")]
    public async Task Execute_MixedTypesAndAlias_ShouldMatchPlacementAndPageAsync(string suffix, string expected)
    {
        // Arrange: reverse insertion order makes physical order unusable as a tie break.
        await using var engine = DocumentDatabaseEngine.Create("ordering", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string[] ids = ["m", "n", "f", "t", "l", "h", "s", "a", "o"];
        string[] values = ["{}", "{\"v\":null}", "{\"v\":false}", "{\"v\":true}", "{\"v\":-1}", "{\"v\":1}", "{\"v\":\"a\"}", "{\"v\":[]}", "{\"v\":{}}"];
        for (int i = ids.Length - 1; i >= 0; i--)
        { await collection.PutAsync(session, ids[i], Encoding.UTF8.GetBytes(values[i]), cancellationToken: CancellationToken.None); }

        // Act / Assert: full results plus an aliased, multi-key page.
        (await IdsAsync(session, $"SELECT META().id AS id, v AS k FROM items ORDER BY k {suffix}")).ShouldBe(expected.Split(','));
        (await IdsAsync(session, $"SELECT META().id AS id, v AS k FROM items ORDER BY k {suffix}, META().id DESC NULLS LAST LIMIT 4 OFFSET 1"))
            .ShouldBe(expected.Split(',').Skip(1).Take(4));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Equal keys use later keys then document identity in either direction (#1730)")]
    public async Task Execute_EqualKeys_ShouldPreserveIdentityAndSecondaryPlacementAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ordering-ties", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('z', {'v':1,'n':NULL}), ('b', {'v':1}), ('a', {'v':1}), ('c', {'v':1,'n':2})", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await IdsAsync(session, "SELECT META().id FROM items ORDER BY v DESC NULLS FIRST, n DESC NULLS FIRST"))
            .ShouldBe(["z", "a", "b", "c"]);
        (await IdsAsync(session, "SELECT META().id FROM items ORDER BY v DESC NULLS LAST LIMIT 2 OFFSET 1"))
            .ShouldBe(["b", "c"]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Direct AST refuses invalid null placement (#1730)")]
    public void Plan_InvalidPlacement_ShouldUseBindingDiagnostic()
    {
        // Arrange
        var query = new OqlQueryParser().Parse("SELECT v FROM c ORDER BY v").ShouldBeOfType<OqlQueryStatement>().OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var invalid = new OqlSelectExpression("c", null, query.Projections, null, [], null,
            [new(query.OrderBy[0].Expression, false, (OqlNullPlacement)99)], false, false);

        // Act / Assert
        Should.Throw<DocumentPlanningException>(() => DocumentPlanner.CreateLogicalPlan(invalid)).Diagnostics[0].Code.ShouldBe("CD01032");
    }

    private static async Task<string[]> IdsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var ids = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { ids.Add((string)row.GetValue(0)!); }
        return ids.ToArray();
    }
}
