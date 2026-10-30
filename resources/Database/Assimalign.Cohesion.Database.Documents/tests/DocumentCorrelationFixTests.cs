using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentCorrelationFixTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: UNNEST sources bind captures before introducing their variable")]
    [InlineData("v")]
    [InlineData("d")]
    public async Task ExecuteAsync_CapturedUnnestSource_ShouldPreserveScanResultsAsync(string variable)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("captured-unnest", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1,tags:[1],d:{tags:[1]}}), ('b', {n:2,d:{tags:[2]}})", cancellationToken: CancellationToken.None);
        string query = "SELECT RAW (SELECT RAW s.n FROM items s UNNEST d.tags AS " + variable + " WHERE " + variable + " = 1 ORDER BY s.n) FROM items d WHERE d.n = 1";
        // Act / Assert
        foreach (bool indexed in new[] { false, true })
        {
            if (indexed) { await session.ExecuteAsync("CREATE INDEX ix ON items (DISTINCT ARRAY v FOR v IN d.tags END)", cancellationToken: CancellationToken.None); }
            var rows = await DocumentSubqueryTests.RowsAsync(session, query);
            ((JsonElement)rows.Single().GetValue(0)!).GetRawText().ShouldBe("[1,2]");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: captured paths never constrain local scalar or array indexes")]
    [InlineData("d.n = 1", "d.n", "s.n")]
    [InlineData("d.n BETWEEN 1 AND 1", "d.n", "s.n")]
    [InlineData("ANY v IN d.tags SATISFIES v = 1 END", "DISTINCT ARRAY v FOR v IN d.tags END", "s.n")]
    [InlineData("d.n IS NOT MISSING", "d.n", "d.n DESC")]
    [InlineData("d.n IS NOT NULL", "d.n", "d.n DESC NULLS LAST")]
    [InlineData("d.n = 1", "d.n, n", "s.n DESC")]
    [InlineData("d.n = 1", "n INCLUDE MISSING", "s.n DESC")]
    public async Task ExecuteAsync_CapturedIndexPath_ShouldPreserveScanResultsAsync(string predicate, string index, string order)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("captured-index", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1,tags:[1],d:{n:1,tags:[1]}}), ('b', {n:2,d:{n:2,tags:[2]}})", cancellationToken: CancellationToken.None);
        string query = "SELECT RAW (SELECT RAW s.n + 0 FROM items s WHERE " + predicate + " ORDER BY " + order + ") FROM items d WHERE d.n = 1";
        // Act
        var control = await DocumentSubqueryTests.RowsAsync(session, query);
        await session.ExecuteAsync("CREATE INDEX ix ON items (" + index + ")", cancellationToken: CancellationToken.None);
        var actual = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        string expected = order.StartsWith("s.n DESC", StringComparison.Ordinal) ? "[2,1]" : "[1,2]";
        ((JsonElement)control.Single().GetValue(0)!).GetRawText().ShouldBe(expected);
        ((JsonElement)actual.Single().GetValue(0)!).GetRawText().ShouldBe(expected);
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + query);
        if (index == "n INCLUDE MISSING")
        { plan.ShouldContain(row => row.GetString(5) != null && row.GetString(5)!.Contains("consumed=True", StringComparison.Ordinal)); }
        else
        {
            plan.ShouldNotContain(row => row.GetString(5) != null && row.GetString(5)!.Contains("consumed=True", StringComparison.Ordinal));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: outer metadata is constant and keeps ascending inner identity ties")]
    [InlineData("META(d).id DESC", "[\"a\"]")]
    [InlineData("META(d).id DESC NULLS LAST", "[\"a\"]")]
    [InlineData("META(d).version DESC", "[\"a\"]")]
    [InlineData("META(s).id DESC", "[\"c\"]")]
    [InlineData("META().id DESC", "[\"c\"]")]
    public async Task ExecuteAsync_MetadataOrdering_ShouldRespectReceiverAsync(string order, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("metadata-order", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW (SELECT RAW META(s).id FROM items s ORDER BY " + order + " LIMIT 1) FROM items d LIMIT 1");
        // Assert
        ((JsonElement)rows.Single().GetValue(0)!).GetRawText().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Correlation: distinct metadata proofs compare receivers")]
    public async Task ExecuteAsync_MetadataEquivalence_ShouldRefuseDifferentReceiversAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("metadata-equivalence", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT RAW (SELECT DISTINCT META(s).id FROM items s ORDER BY META(d).id) FROM items d", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01014");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Correlation: aggregate input cannot capture its enclosing GROUP AS")]
    public async Task ExecuteAsync_GroupArrayCaptureInAggregate_ShouldMatchDirectRefusalAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("group-array-input", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT SUM(FIRST v FOR v IN (SELECT RAW ARRAY_LENGTH(g) FROM [1] AS s) END) FROM [{n:1}] AS d GROUP BY d.n GROUP AS g", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01031");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: each enclosing grouped scope refuses hidden source paths")]
    [InlineData("SELECT (SELECT RAW d.n FROM [1] AS s) FROM [{g:1,n:10},{g:1,n:20}] AS d GROUP BY d.g")]
    [InlineData("SELECT COUNT(*) FROM [{g:1,n:10}] AS d GROUP BY d.g HAVING EXISTS (SELECT RAW d.n FROM [1] AS s)")]
    [InlineData("SELECT COUNT(*) FROM [{g:1,n:10}] AS d GROUP BY d.g ORDER BY (SELECT RAW d.n FROM [1] AS s)")]
    [InlineData("SELECT (SELECT RAW (SELECT RAW d.n FROM [1] AS t) FROM [1] AS s) FROM [{g:1,n:10}] AS d GROUP BY d.g")]
    [InlineData("SELECT (SELECT RAW x FROM d AS x) FROM [{g:1,n:10}] AS d GROUP BY d.g")]
    [InlineData("SELECT (SELECT RAW x FROM d.n AS x) FROM [{g:1,n:10}] AS d GROUP BY d.g")]
    [InlineData("SELECT (SELECT RAW u FROM [1] AS s) FROM [{g:1,tags:[10]}] AS d UNNEST d.tags AS u GROUP BY d.g")]
    [InlineData("SELECT (SELECT RAW META(d).id FROM items s) FROM items d GROUP BY d.n")]
    [InlineData("SELECT COUNT(*), (SELECT RAW d.n FROM [1] AS s) FROM [{g:1,n:10}] AS d")]
    [InlineData("SELECT (SELECT RAW SUM(d.n) FROM [1] AS s) FROM [{g:1,n:10}] AS d GROUP BY d.g")]
    [InlineData("SELECT (SELECT RAW d.n FROM [1] AS s) FROM [{n:10}] AS d GROUP BY d.n + 1")]
    [InlineData("SELECT (SELECT RAW (SELECT RAW s.n FROM [1] AS t) FROM [{g:1,n:10}] AS s GROUP BY s.g) FROM [1] AS d")]
    [InlineData("SELECT RAW ARRAY (SELECT RAW d.n + x FROM [1] AS s) FOR x IN [0] END FROM [{n:10,x:1},{n:11,x:0}] AS d UNNEST [d.x] AS x GROUP BY (SELECT RAW d.n + x FROM [1] AS s)")]
    public async Task ExecuteAsync_UngroupedCapture_ShouldMatchDirectRefusalAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("grouped-captures", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
        error.Diagnostics.Single().Start.ShouldNotBeNull();
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: group keys bindings aggregate inputs and shadowed sources remain available")]
    [InlineData("SELECT RAW (SELECT RAW d.g FROM [1] AS s) FROM [{g:1,n:10},{g:1,n:20}] AS d GROUP BY d.g", "[1]")]
    [InlineData("SELECT RAW (SELECT RAW k FROM [1] AS s) FROM [{g:1,n:10}] AS d GROUP BY d.g AS k", "[1]")]
    [InlineData("SELECT RAW (SELECT RAW ARRAY_LENGTH(g) FROM [1] AS s) FROM [{g:1,n:10},{g:1,n:20}] AS d GROUP BY d.g GROUP AS g", "[2]")]
    [InlineData("SELECT RAW SUM(FIRST v FOR v IN (SELECT RAW d.n FROM [1] AS s) END) FROM [{g:1,n:10},{g:1,n:20}] AS d GROUP BY d.g", "30")]
    [InlineData("SELECT RAW (SELECT RAW d.n FROM [{n:9}] AS d) FROM [{g:1,n:10}] AS d GROUP BY d.g", "[9]")]
    [InlineData("SELECT RAW (SELECT RAW d.n + 1 FROM [1] AS s) FROM [{n:10}] AS d GROUP BY d.n + 1", "[11]")]
    [InlineData("SELECT RAW (SELECT RAW ARRAY (SELECT RAW d.n FROM [1] AS t) FOR d IN [{n:9}] END FROM [1] AS s) FROM [{g:1,n:10}] AS d GROUP BY d.g", "[[[9]]]")]
    [InlineData("SELECT RAW (SELECT RAW v FROM g AS v) FROM [{g:1,n:10}] AS d GROUP BY d.g GROUP AS g", "[{\"d\":{\"g\":1,\"n\":10}}]")]
    [InlineData("SELECT RAW (SELECT {n:9} AS d FROM [1] AS s ORDER BY (SELECT RAW d.n FROM [1] AS t)) FROM [{g:1,n:10}] AS d GROUP BY d.g", "[{\"d\":{\"n\":9}}]")]
    [InlineData("SELECT RAW (SELECT RAW d.n FROM [1] AS s) FROM [{n:10}] AS d GROUP BY (SELECT RAW d.n FROM [1] AS s)", "[10]")]
    [InlineData("SELECT RAW SUM(FIRST v FOR v IN (SELECT RAW g FROM [9] AS g) END) FROM [{n:1}] AS d GROUP BY d.n GROUP AS g", "9")]
    [InlineData("SELECT RAW ARRAY (SELECT RAW d.n FROM [1] AS s) FOR s IN [0] END FROM [{n:10}] AS d GROUP BY (SELECT RAW d.n FROM [1] AS s)", "[[10]]")]
    public async Task ExecuteAsync_GroupedCapture_ShouldAllowVisibleBindingsAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("valid-grouped-captures", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        var value = rows.Single().GetValue(0);
        (value is JsonElement json ? json.GetRawText() : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);
    }

}
