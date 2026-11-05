using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentCompoundRegressionTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Compounds: a small-stack refusal is CD05021 and leaves the session usable")]
    public async Task ExecuteAsync_SmallStack_ShouldCodeFailureAndReuseSessionAsync()
    {
        // Arrange: parse on the ordinary stack so this exercises the engine boundary.
        await using var engine = DocumentDatabaseEngine.Create("compound-stack", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = DocumentQueryRequest.FromOql(string.Join(" UNION ALL ", Enumerable.Repeat("SELECT RAW 1 FROM [0] AS z", 129)));
        Exception? failure = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var thread = new Thread(() =>
        {
            try { session.ExecuteAsync(request, cancellation.Token).AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; } // Record the exact public failure from the worker.
        }, 256 * 1024) { IsBackground = true };
        // Act
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        // Assert
        var error = failure.ShouldBeOfType<DatabaseException>();
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05021");
        error.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
        (await ValuesAsync(session, "SELECT RAW 7 FROM [0] AS z")).ShouldBe(new[] { "7" });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Compounds: deep arrays return complete rows or a coded stack refusal")]
    [InlineData(64, false)]
    [InlineData(96, false)]
    [InlineData(64, true)]
    public async Task ExecuteAsync_DeepArrayOnSmallStack_ShouldSurviveAsync(int operators, bool explain)
    {
        // Arrange: this shape killed the Debug baseline probe on a 1 MiB stack; Release can fit.
        await using var engine = DocumentDatabaseEngine.Create("compound-array-stack", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string query = "SELECT RAW " + new string('[', 118) + "1" + new string(']', 118) + " FROM [0] AS z";
        for (int level = 0; level < 8; level++) { query = "SELECT RAW (" + query + ") FROM [0] AS a"; }
        query += string.Concat(Enumerable.Repeat(" UNION ALL SELECT RAW 1 FROM [0] AS b", operators));
        var request = DocumentQueryRequest.FromOql((explain ? "EXPLAIN " : "") + query);
        Exception? failure = null;
        int completedRows = 0, arrayDepth = 0, leaf = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var thread = new Thread(() =>
        {
            try
            {
                var result = session.ExecuteAsync(request, cancellation.Token).AsTask().GetAwaiter().GetResult();
                if (result is DatabaseResultSet rows)
                {
                    var iterator = rows.GetRowsAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
                    try
                    {
                        while (iterator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                        {
                            if (!explain && completedRows == 0)
                            {
                                var value = (JsonElement)iterator.Current.GetValue(0)!;
                                while (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1)
                                { arrayDepth++; value = value[0]; }
                                leaf = value.GetInt32();
                            }
                            completedRows++;
                        }
                    }
                    finally
                    {
                        iterator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        rows.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception error) { failure = error; } // Transfer the observed outcome to the test thread.
        }, 1024 * 1024) { IsBackground = true };
        // Act
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        // Assert: stack use varies by JIT/platform; either outcome must preserve the full contract.
        if (failure is not null) { failure.ShouldBeOfType<DatabaseException>().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05021"); }
        else if (explain) { completedRows.ShouldBeGreaterThan(0); }
        else
        {
            completedRows.ShouldBe(operators + 1);
            arrayDepth.ShouldBe(126);
            leaf.ShouldBe(1);
        }
        (await ValuesAsync(session, "SELECT RAW 7 FROM [0] AS z")).ShouldBe(new[] { "7" });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Compounds: a sibling capture does not hide the local operand's index seek")]
    public async Task ExecuteAsync_SiblingCapture_ShouldKeepLocalIndexSeekAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-capture-index", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("coll", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO coll (KEY,VALUE) VALUES ('a',{k:1,v:2})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_k ON coll (k)", cancellationToken: CancellationToken.None);
        const string query = "SELECT RAW (SELECT RAW d.v FROM coll AS d WHERE d.k = 1 UNION ALL SELECT RAW d.k FROM [0] AS z) FROM [{k:1}] AS d";
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN " + query, cancellationToken: CancellationToken.None);
        var operators = new List<string?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { operators.Add(row.GetString(2)); }
        // Assert
        operators.ShouldContain("IndexSeek");
        (await ValuesAsync(session, query)).ShouldBe(new[] { "[2,1]" });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - IN: wrapped compound values and compound membership retain separate readings")]
    [InlineData("((SELECT RAW 1 FROM [0] AS a UNION SELECT RAW 3 FROM [0] AS b))", "")]
    [InlineData("(((SELECT RAW 1 FROM [0] AS a) UNION SELECT RAW 3 FROM [0] AS b))", "1|3")]
    public async Task ExecuteAsync_DoublyWrappedCompound_ShouldPinMembershipReadingAsync(string operand, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-in", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        string.Join("|", await ValuesAsync(session, "SELECT RAW x FROM [1,2,3] AS x WHERE x IN " + operand)).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Compounds: VALUES and mutation expressions execute complete subqueries")]
    [InlineData("INSERT INTO dest (KEY,VALUE) VALUES ('b',{n:(SELECT RAW x FROM [1,2] AS x EXCEPT SELECT RAW 2 FROM [0] AS y)})", "{\"n\":[1]}")]
    [InlineData("UPDATE dest SET n = (SELECT RAW x FROM [1,2] AS x INTERSECT SELECT RAW 1 FROM [0] AS y)", "{\"n\":[1]}")]
    [InlineData("UPDATE dest SET n = 9 WHERE n IN (SELECT RAW x FROM [1,2] AS x EXCEPT SELECT RAW 2 FROM [0] AS y)", "{\"n\":9}")]
    [InlineData("DELETE FROM dest WHERE n IN (SELECT RAW x FROM [1,2] AS x INTERSECT SELECT RAW 1 FROM [0] AS y)", "")]
    public async Task ExecuteAsync_MutationPosition_ShouldEvaluateCompoundAsync(string text, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-mutations", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("dest", CancellationToken.None);
        if (!text.StartsWith("INSERT", StringComparison.Ordinal))
        { await session.ExecuteAsync("INSERT INTO dest (KEY,VALUE) VALUES ('a',{n:1})", cancellationToken: CancellationToken.None); }
        // Act
        var result = await session.ExecuteAsync(text, cancellationToken: CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(1);
        string.Join("|", await ValuesAsync(session, "SELECT RAW dest FROM dest")).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Compounds: RETURNING reads a complete compound subquery before publication")]
    [InlineData("INSERT INTO dest (KEY,VALUE) VALUES ('b',{n:2}) RETURNING RAW (SELECT RAW d.n FROM dest AS d UNION SELECT RAW 3 FROM [0] AS z)", "[1,3]")]
    [InlineData("UPDATE dest SET n = 2 RETURNING RAW (SELECT RAW d.n FROM dest AS d INTERSECT SELECT RAW 1 FROM [0] AS z)", "[1]")]
    [InlineData("DELETE FROM dest RETURNING RAW (SELECT RAW d.n FROM dest AS d EXCEPT SELECT RAW 2 FROM [0] AS z)", "[1]")]
    public async Task ExecuteAsync_ReturningPosition_ShouldKeepStatementStartReadAsync(string text, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("dest", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO dest (KEY,VALUE) VALUES ('a',{n:1})", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await ValuesAsync(session, text)).ShouldBe(new[] { expected });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Compounds: UNNEST expands compound subquery values")]
    public async Task ExecuteAsync_UnnestPosition_ShouldExpandCompoundAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-unnest", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await ValuesAsync(session, "SELECT RAW n FROM [0] AS seed UNNEST (SELECT RAW x FROM [1,2,3] AS x EXCEPT SELECT RAW 2 FROM [0] AS y) AS n")).ShouldBe(new[] { "1", "3" });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Multisets: repeated right keys and ALL decrements use one comparison walk")]
    [InlineData("INTERSECT ALL", 1024)]
    [InlineData("EXCEPT ALL", 0)]
    public async Task ExecuteAsync_ExistingRightKeys_ShouldUseOneProbeAsync(string operation, int expected)
    {
        // Arrange: new keys still perform lookup then insertion; repeats and decrements do not.
        var builder = DocumentDatabaseEngine.CreateBuilder("compound-count-work");
        builder.Options.StatementWorkBudget = 90_000;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var rows = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 1024)) + "]");
        using var repeated = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 4096).Select(index => index % 1024)) + "]");
        var parameters = new Dictionary<string, object?> { ["rows"] = rows.RootElement, ["repeated"] = repeated.RootElement };
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT RAW x FROM @rows AS x " + operation + " SELECT RAW y FROM @repeated AS y", parameters, CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
        // Assert
        count.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Multisets: grouped multiplicities follow independent min and subtraction formulas")]
    [InlineData("INTERSECT")]
    [InlineData("EXCEPT")]
    public async Task ExecuteAsync_GroupCounts_ShouldMatchArithmeticOracleAsync(string operation)
    {
        // Arrange: each value has an independently selected left and right multiplicity.
        int[] leftCounts = [0, 1, 2, 5, 4, 7], rightCounts = [3, 0, 2, 3, 8, 1];
        int[] left = Enumerable.Range(0, leftCounts.Length).SelectMany(value => Enumerable.Repeat(value, leftCounts[value])).ToArray();
        int[] right = Enumerable.Range(0, rightCounts.Length).SelectMany(value => Enumerable.Repeat(value, rightCounts[value])).ToArray();
        await using var engine = DocumentDatabaseEngine.Create("compound-count-oracle", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string query = "SELECT RAW x FROM [" + string.Join(",", left) + "] AS x " + operation + " ALL SELECT RAW y FROM [" + string.Join(",", right) + "] AS y";
        // Act
        var rows = await ValuesAsync(session, query);
        var counts = rows.GroupBy(value => int.Parse(value, CultureInfo.InvariantCulture)).ToDictionary(group => group.Key, group => group.Count());
        // Assert: no counter-consumption or representative selection algorithm is shared.
        foreach (int value in Enumerable.Range(0, leftCounts.Length))
        { counts.GetValueOrDefault(value).ShouldBe(operation == "INTERSECT" ? Math.Min(leftCounts[value], rightCounts[value]) : Math.Max(leftCounts[value] - rightCounts[value], 0)); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Multisets: ordered paged nested rows agree with an arithmetic oracle")]
    [InlineData("INTERSECT ALL", "2|1")]
    [InlineData("EXCEPT ALL", "3|1")]
    public async Task ExecuteAsync_OrderedNestedPage_ShouldMatchOracleRowsAsync(string operation, string expected)
    {
        // Arrange: left counts {1:3,2:1,3:2,4:1}; right {1:1,2:2,3:1,4:0}.
        await using var engine = DocumentDatabaseEngine.Create("compound-page-oracle", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string query = "SELECT RAW q.n FROM ((SELECT x AS n FROM [4,1,3,1,2,3,1] AS x UNION ALL SELECT 0 AS n FROM [] AS z) " + operation +
            " SELECT y AS n FROM [1,2,2,3] AS y ORDER BY n DESC LIMIT 2 OFFSET 1) AS q";
        // Act / Assert
        string.Join("|", await ValuesAsync(session, query)).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - EXPLAIN: multiset parents carry static shape and resolved paging")]
    [InlineData("INTERSECT", "Intersect")]
    [InlineData("INTERSECT ALL", "IntersectAll")]
    [InlineData("EXCEPT", "Except")]
    [InlineData("EXCEPT ALL", "ExceptAll")]
    public async Task ExecuteAsync_ExplainParent_ShouldDescribeShapeAndPageAsync(string operation, string name)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("compound-explain-page", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN SELECT 1 AS n FROM [1/0] AS x " + operation +
            " SELECT 2 AS n FROM [1/0] AS y ORDER BY n LIMIT 3 OFFSET 2", cancellationToken: CancellationToken.None);
        var parents = new List<string?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { if (row.GetString(2) == name) { parents.Add(row.GetString(5)); } }
        // Assert
        parents.ShouldBe(new[] { "shape=Columns; order_keys=1; limit=3; offset=2" });
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    private static async Task<List<string>> ValuesAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var values = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetString(0)!); }
        return values;
    }
}
