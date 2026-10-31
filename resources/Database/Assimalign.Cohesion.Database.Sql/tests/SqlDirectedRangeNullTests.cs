using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlDirectedRangeNullTests
{
    private readonly ITestOutputHelper _output;

    public SqlDirectedRangeNullTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Directed ranges: one-sided comparisons exclude the NULL block from estimates and visits (#1631)")]
    [InlineData("", false)]
    [InlineData("DESC", false)]
    [InlineData("ASC NULLS FIRST", false)]
    [InlineData("DESC NULLS LAST", false)]
    [InlineData("", true)]
    [InlineData("DESC", true)]
    [InlineData("ASC NULLS FIRST", true)]
    [InlineData("DESC NULLS LAST", true)]
    public async Task ExecuteAsync_NullHeavyOneSidedRanges_ShouldVisitOnlyMatches(string order, bool equalityPrefix)
    {
        // Arrange: 99.9% NULL; trailing keys exercise whole-component boundaries.
        const int rows = 100_000;
        await using var engine = SqlDatabaseEngine.Create("directed-null-ranges", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute("CREATE TABLE t(id INT,bucket INT,deleted_at TIMESTAMP)");
        for (int start = 0; start < rows; start += 500)
        {
            await Execute("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(start, 500).Select(id =>
                $"({id},1," + (id < 100 ? $"TIMESTAMP '2026-01-01' + INTERVAL '{id - 50} days'" : "NULL") + ")")));
        }
        // Adjacent prefixes must stay outside the range even when their value qualifies.
        await Execute("INSERT INTO t VALUES(-1,0,TIMESTAMP '2026-01-01'),(-2,2,TIMESTAMP '2026-01-01'),(-3,NULL,NULL)");
        await Execute("CREATE TABLE plain AS SELECT * FROM t");
        await Execute("CREATE INDEX ix ON t(" + (equalityPrefix ? "bucket DESC NULLS FIRST," : "") + "deleted_at " + order + ",id DESC)");
        string prefix = equalityPrefix ? "bucket=1 AND " : "";
        var observations = new List<BTreeRangeCount>();
        var failures = new List<string>();

        // Act: compare both logical open sides, inclusive and exclusive, to a full scan.
        foreach (string comparison in new[] { ">", ">=", "<", "<=" })
        {
            string predicate = prefix + "deleted_at " + comparison + " TIMESTAMP '2026-01-01'";
            var expected = await SqlPartialIndexTests.Rows(session, "SELECT id FROM plain WHERE " + predicate + " ORDER BY id");
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(rows + 3);
            observations.Clear();
            object?[][] actual;
            SqlPlanExecutor.CostProbeCompleted = observations.Add;
            try { actual = await SqlPartialIndexTests.Rows(session, "SELECT id FROM t WHERE " + predicate + " ORDER BY id"); }
            finally { SqlPlanExecutor.CostProbeCompleted = null; }
            actual.Select(row => row[0]).ShouldBe(expected.Select(row => row[0]), predicate);
            var metrics = session.LastStatementMetrics!;
            _output.WriteLine($"{order}; prefix={equalityPrefix}; {comparison}: matches={actual.Length}, path={metrics.AccessPath}, visits={metrics.RecordsExamined}; probes=" +
                string.Join(';', observations.Select(count => $"{count.Count}/{count.IsComplete}/{count.EntriesExamined}")));
            if (metrics.AccessPath != "seek:ix" || metrics.RecordsExamined != expected.Length ||
                observations.Count == 0 || observations[0].Count != expected.Length || !observations[0].IsComplete)
            { failures.Add($"{comparison}: {metrics.AccessPath}, visits={metrics.RecordsExamined}, first estimate={observations.FirstOrDefault()}"); }

            // Bypass costing as well: every physical candidate must satisfy the comparison.
            var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT id FROM t WHERE " + predicate).Statement.SqlExpression)
                .ShouldBeOfType<SqlSelectPlan>();
            var seek = plan.Access.ShouldBeOfType<SqlIndexSeekPath>();
            int[] ordinals = equalityPrefix ? [1, 2, 0] : [2, 0];
            var range = SqlIndexKeyEncoding.BuildSeekRange(plan.Table, seek, ordinals, database.Catalog.DefaultCollation);
            database.IndexManager.TryGetIndex(plan.Table.ObjectId, "ix", out var index).ShouldBeTrue();
            await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
            var candidates = new List<int>();
            await using (var cursor = index.OpenCursor(transaction.Context, range))
            {
                while (await cursor.MoveNextAsync(CancellationToken.None))
                {
                    var (page, slot) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);
                    database.DataStorage.TryReadRecord(page, slot, plan.Table.ObjectId, out var record).ShouldBeTrue();
                    candidates.Add((int)SqlRowCodec.Decode(record.Span, plan.Table, out _, out _, out _)[0]!);
                }
            }
            await transaction.RollbackAsync(CancellationToken.None);
            if (!candidates.Order().Cast<object?>().SequenceEqual(expected.Select(row => row[0])))
            { failures.Add($"{comparison}: physical cursor visited {candidates.Count}, expected {expected.Length}"); }
        }

        // Assert: NULL equality prefixes still reach the complete NULL block.
        string nullPredicate = prefix + "deleted_at IS NULL";
        var nullExpected = await SqlPartialIndexTests.Rows(session, "SELECT id FROM plain WHERE " + nullPredicate + " ORDER BY id");
        var nullActual = await SqlPartialIndexTests.Rows(session, "SELECT id FROM t WHERE " + nullPredicate + " ORDER BY id");
        nullActual.Select(row => row[0]).ShouldBe(nullExpected.Select(row => row[0]));
        nullActual.Length.ShouldBe(equalityPrefix ? 99_900 : 99_901);
        failures.ShouldBeEmpty();

        async Task Execute(string sql) => await SqlPartialIndexTests.Execute(session, sql);
    }
}
