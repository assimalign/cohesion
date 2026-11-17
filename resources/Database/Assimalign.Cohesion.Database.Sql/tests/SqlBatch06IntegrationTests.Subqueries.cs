using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlBatch06IntegrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Consumed scalar and EXISTS spools leave all eight client-result slots available (#1789)")]
    [InlineData(false, 0)] [InlineData(false, 8)]
    [InlineData(true, 0)] [InlineData(true, 8)]
    public async Task ExecuteAsync_ConsumedSubqueryWithRetainedResults_ShouldLeaveClientSlotsFree(bool scalar, int liveResults)
    {
        // Arrange: the ordered child must spool even though its consumer returns one value.
        var builder = SqlDatabaseEngine.CreateBuilder("batch06-subquery-slots");
        builder.Options.TempDirectory = Path.Combine(_root, "subqueries");
        builder.Options.StatementMemoryBudget = 262144;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await (await engine.CreateDatabaseAsync("db", CancellationToken.None)).CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        for (int first = 0; first < 1024; first += 128)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i})")), cancellationToken: CancellationToken.None); }
        var retained = new List<DatabaseResultSet>();
        try
        {
            for (int slot = 0; slot < liveResults; slot++)
            {
                var held = await session.ExecuteAsync("SELECT id,RPAD(CAST(id AS TEXT),128,'x') AS wide FROM t ORDER BY wide,id", cancellationToken: CancellationToken.None);
                retained.Add(held.ShouldBeOfType<SqlSpilledResultSet>());
            }
            long heldBytes = TempBytes(engine.TempSpace!);
            int heldFiles = Directory.Exists(engine.TempSpace!.DirectoryPath)
                ? Directory.EnumerateFiles(engine.TempSpace.DirectoryPath, "*.ctmp", SearchOption.AllDirectories).Count() : 0;
            const string child = "SELECT RPAD(CAST(id AS TEXT),128,'x') FROM t ORDER BY id,RPAD(CAST(id AS TEXT),128,'x')";

            // Act
            string sql = scalar ? "SELECT (" + child + " LIMIT 1 OFFSET 1023) AS answer" : "SELECT EXISTS (" + child + ") AS answer";
            var values = new List<object?>();
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None))
            { await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); } }

            // Assert: only the held client results survive the child reader's disposal.
            values.ShouldHaveSingleItem().ShouldBe(scalar ? (object)"1023".PadRight(128, 'x') : true);
            SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull().PeakBytes.ShouldBeGreaterThan(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            var registry = SessionOwners(null)!.GetValue(session, _ => throw new InvalidOperationException());
            OwnedResults(registry).Count.ShouldBe(liveResults);
            TempBytes(engine.TempSpace!).ShouldBe(heldBytes);
            Directory.EnumerateFiles(engine.TempSpace!.DirectoryPath, "*.ctmp", SearchOption.AllDirectories).Count().ShouldBe(heldFiles);
        }
        finally { foreach (var held in retained) { await held.DisposeAsync(); } }
        TempBytes(engine.TempSpace!).ShouldBe(0);
        Directory.EnumerateFiles(engine.TempSpace!.DirectoryPath, "*.ctmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }
}
