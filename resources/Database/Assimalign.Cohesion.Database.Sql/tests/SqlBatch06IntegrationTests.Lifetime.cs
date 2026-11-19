using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlBatch06IntegrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Combined ANALYZE attributes spill fields to executing operators rather than sequence wrappers (#1789)")]
    [InlineData(0, 2097152)] [InlineData(1, 262144)]
    public async Task ExecuteAsync_CombinedSpillingExplain_ShouldAttributeOnlyOperatorSpills(int shape, long grant)
    {
        // Arrange / Act
        var actual = await ExecuteAsync(shape, grant, false, "EXPLAIN ANALYZE ");
        _output.WriteLine(actual.Detail);

        // Assert: wrappers and access paths never acquire another operator's spill counters.
        actual.Code.ShouldBe("ok", actual.Detail);
        actual.SpillBytes.ShouldBeGreaterThan(0);
        var lines = actual.Detail.Split('\n');
        var spillRows = lines.Where(line => line.Contains("spilled_bytes=", StringComparison.Ordinal)).ToArray();
        spillRows.ShouldNotBeEmpty();
        foreach (string line in spillRows)
        {
            string operation = line.Split(';')[2];
            new[] { "String:Select", "String:Union", "String:WindowAgg", "String:NestedLoopJoin", "String:JoinStep" }
                .ShouldContain(operation, line);
        }
        foreach (string line in lines.Where(line => line.Split(';')[2] is "String:Sequences" or "String:With" or "String:DerivedTable" or "String:TableScan"))
        { line.ShouldNotContain("spilled_bytes=", Case.Sensitive); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Combined stages count only returned client spools toward the eight-result cap (#1789)")]
    [InlineData(false, 7)] [InlineData(true, 7)] [InlineData(true, 8)]
    public async Task ExecuteAsync_CombinedStagesWithRetainedResults_ShouldChargeOnlyClientResults(bool tableAs, int liveResults)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("batch06-slots");
        builder.Options.TempDirectory = Path.Combine(_root, "slots");
        builder.Options.StatementMemoryBudget = tableAs ? 1048576 : 262144;
        builder.Functions.Add(SqlScalarFunction.Create("tap", (long value) => value));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        for (int first = 0; first < 1024; first += 128)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i})"))); }
        await session.ExecuteAsync("CREATE TABLE o(id INT)"); await session.ExecuteAsync("INSERT INTO o VALUES(0),(1)");
        await session.ExecuteAsync("CREATE SEQUENCE s");
        var retained = new List<DatabaseResultSet>();
        bool sourceSpilled = false;
        if (tableAs) { SqlPlanExecutor.TableAsSourceReading = (_, _) => sourceSpilled = true; }
        try
        {
            for (int slot = 0; slot < liveResults; slot++)
            {
                var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT id,RPAD(CAST(id AS TEXT),{(tableAs ? 1024 : 256)},'x') AS wide FROM t");
                result.ShouldBeOfType<SqlSpilledResultSet>(); retained.Add(result);
            }

            // Act: CTAS needs no client slot; SELECT publishes the eighth result.
            string sql = tableAs
                ? Query(2).Replace(" AS seq FROM", " AS seq,RPAD(CAST(id AS TEXT),256,'x') AS wide FROM", StringComparison.Ordinal)
                : Query(1);
            var combined = await session.ExecuteAsync(sql);
            if (combined is DatabaseResultSet set)
            {
                retained.Add(set);
                await foreach (var _ in set.GetRowsAsync()) { }
                set.ShouldBeOfType<SqlSpilledResultSet>();
            }

            // Assert: internal owners disappear at statement completion.
            if (tableAs) { sourceSpilled.ShouldBeTrue(); }
            var registry = SessionOwners(null)!.GetValue(session, _ => throw new InvalidOperationException());
            OwnedResults(registry).Count.ShouldBe(tableAs ? liveResults : liveResults + 1);
            while (retained.Count < 8)
            {
                retained.Add((DatabaseResultSet)await session.ExecuteAsync("SELECT id,RPAD(CAST(id AS TEXT),1024,'x') AS wide FROM t"));
            }
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("SELECT id,RPAD(CAST(id AS TEXT),1024,'x') AS wide FROM t"));
            failure.Diagnostics[0].Code.ShouldBe("CS05067");
        }
        finally
        {
            SqlPlanExecutor.TableAsSourceReading = null;
            foreach (var result in retained) { await result.DisposeAsync(); }
        }
        TempBytes(engine.TempSpace!).ShouldBe(0);
        Directory.EnumerateFiles(engine.TempSpace!.DirectoryPath, "*.ctmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_sessions")]
    private static extern ref ConditionalWeakTable<SqlDatabaseSession, SqlSpillLifetime>? SessionOwners(SqlSpillLifetime? declaringType);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_results")]
    private static extern ref HashSet<SqlSpilledResultSet> OwnedResults(SqlSpillLifetime lifetime);
}
