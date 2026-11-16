using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: volatile window sort pins default-grant row capacity (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_VolatileWindowSort_ShouldPinDefaultCapacity(bool forceBase)
    {
        // Arrange: sources and sorted duplicate the window ledger's retained rows.
        const int fits = 101836;
        var builder = SqlDatabaseEngine.CreateBuilder("volatile-window-capacity");
        builder.Functions.Add(new MemoryRandomFunction());
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        for (int start = 1; start <= fits + 1; start += 4096)
        {
            int count = Math.Min(4096, fits + 2 - start);
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(start, count).Select(k => $"({k})")), cancellationToken: CancellationToken.None);
        }
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        var parameters = new Dictionary<string, object?> { ["n"] = fits };

        // Act
        (await TryRead("SELECT random(),ROW_NUMBER() OVER(ORDER BY k) FROM t WHERE k<=@n ORDER BY k")).ShouldBeTrue();
        long successPeak = session.LastStatementMetrics!.PeakHashMemoryBytes;
        (await TryRead("SELECT random(),ROW_NUMBER() OVER(ORDER BY k) FROM t ORDER BY k")).ShouldBeFalse();
        long refusalPeak = session.LastStatementMetrics!.PeakHashMemoryBytes;
        await session.ExecuteAsync($"DELETE FROM t WHERE k>{fits}", cancellationToken: CancellationToken.None);
        (await TryRead("SELECT random(),ROW_NUMBER() OVER(ORDER BY k) FROM t ORDER BY k")).ShouldBeTrue();

        // Assert
        _output.WriteLine($"VOLATILE_WINDOW_CAPACITY|base={forceBase}|rows={fits}|peak={successPeak}|refusalPeak={refusalPeak}");
        successPeak.ShouldBe(67108448);
        refusalPeak.ShouldBe(67108740);
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(successPeak);

        async Task<bool> TryRead(string sql)
        {
            try
            {
                await using var rows = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters, CancellationToken.None);
                await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { _ = row.FieldCount; }
                return true;
            }
            catch (DatabaseException error) { error.Diagnostics[0].Code.ShouldBe("CS05067"); return false; }
            finally
            {
                session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
                session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            }
        }
    }
}
