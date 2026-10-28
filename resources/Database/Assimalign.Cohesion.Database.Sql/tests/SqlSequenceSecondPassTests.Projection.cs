using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceSecondPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence OFFSET: skipped tuples evaluate after sorting and before LIMIT")]
    [InlineData("SELECT nextval('s'), k FROM t LIMIT 1 OFFSET 1")]
    [InlineData("SELECT nextval('s'), k FROM t ORDER BY k LIMIT 1 OFFSET 1")]
    [InlineData("SELECT nextval('s'), k FROM t GROUP BY k LIMIT 1 OFFSET 1")]
    [InlineData("SELECT nextval('s'), k FROM t GROUP BY k ORDER BY k LIMIT 1 OFFSET 1")]
    [InlineData("SELECT nextval('s'), (SELECT max(v) FROM u WHERE u.k=t.k) FROM t LIMIT 1 OFFSET 1")]
    [InlineData("SELECT nextval('s'), (SELECT max(v) FROM u WHERE u.k=t.k) FROM t ORDER BY k LIMIT 1 OFFSET 1")]
    [InlineData("SELECT nextval('s'), (SELECT max(v) FROM u WHERE u.k=t.k) v FROM t ORDER BY v LIMIT 1 OFFSET 1")]
    public async Task Execute_SequenceOffset_ShouldConsumeOffsetPlusLimit(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("second-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT, v INT)");
        await Exec(session, "INSERT INTO t VALUES (3),(1),(2)");
        await Exec(session, "INSERT INTO u VALUES (1,1),(2,2),(3,3)");

        // Act
        var rows = await Rows(session, sql);

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(3L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlated deferred projection: retain only filtered top N rows within the statement budget")]
    [InlineData(false)][InlineData(true)]
    public async Task Execute_CorrelatedSequenceTopN_ShouldBoundRetainedCandidates(bool filtered)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("second-pass", new() { StatementMemoryBudget = 8192 });
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT, v INT)");
        await Exec(session, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(1, 4096).Reverse().Select(i => $"({i})")));
        int peak = 0;
        SqlPlanExecutor.SequenceCandidatesRetained = count => peak = Math.Max(peak, count);
        try
        {
            // Act
            var rows = await Rows(session, "SELECT nextval('s'), (SELECT max(v) FROM u WHERE u.k=t.k) FROM t " +
                (filtered ? "WHERE k <= 3 " : "") + "ORDER BY k LIMIT 10");

            // Assert
            rows.Length.ShouldBe(filtered ? 3 : 10);
            _output.WriteLine($"input=4096; filtered={filtered}; peak retained={peak}; charged peak={session.LastStatementMetrics!.PeakHashMemoryBytes}");
            peak.ShouldBe(filtered ? 3 : 10);
            session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeGreaterThan(0);
            session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.SequenceCandidatesRetained = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Deferred sequence retention: large payloads are charged before projection")]
    public async Task Execute_DeferredSequenceInputOverBudget_ShouldRefuseBeforeNextval()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("second-pass", new() { StatementMemoryBudget = 1024 });
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT, payload TEXT)");
        await Exec(session, "CREATE TABLE u(k INT, v INT)");
        await Exec(session, "INSERT INTO t VALUES (1,'" + new string('x', 1024) + "')");

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => Rows(session,
            "SELECT nextval('s'), (SELECT max(v) FROM u WHERE u.k=t.k) FROM t ORDER BY k LIMIT 1"));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(1L);
    }

}
