using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlIdentityReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Identity DDL race: stale lookup is retryable")]
    [InlineData("ALTER TABLE t ALTER id DROP IDENTITY")][InlineData("DROP TABLE t")][InlineData("TRUNCATE t")]
    [InlineData("ALTER TABLE t ALTER id TYPE BIGINT")]
    public async Task IdentitySequence_ChangedAfterTableRead_ShouldReportRetryableFailure(string ddl)
    {
        // Arrange: reproduce the planner's two reads with DDL between them.
        await using var engine = SqlDatabaseEngine.Create("identity-race-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        db.Catalog.TryGetTable("dbo", "absent", out _).ShouldBeFalse();
        await Exec(session, "CREATE TABLE t(id SERIAL)");
        db.Catalog.TryGetTable("dbo", "t", out var previous).ShouldBeTrue();
        await Exec(session, ddl);
        // Act
        var failure = Should.Throw<Catalog.SqlCatalogException>(() => db.Catalog.IdentitySequence(previous.ObjectId, "id"));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS04067");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Identity type change: preserve pending restart and live position")]
    [InlineData("ALTER TABLE t ALTER id RESTART WITH 100", true, 100L)]
    [InlineData("SELECT setval('t_id_seq',100,false)", true, 100L)]
    [InlineData("ALTER SEQUENCE t_id_seq RESTART WITH 100", true, 100L)]
    [InlineData("ALTER TABLE t ALTER id SET START WITH 100", true, 4L)]
    [InlineData("ALTER SEQUENCE t_id_seq START WITH 100", true, 4L)]
    [InlineData("ALTER TABLE t ALTER id SET START WITH 100", false, 1L)]
    [InlineData("ALTER SEQUENCE t_id_seq START WITH 100", false, 1L)]
    public async Task ExecuteAsync_TypeAfterRestart_ShouldPreserveCounter(string change, bool called, long expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("identity-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id SERIAL PRIMARY KEY)");
        if (called) { for (int i = 0; i < 3; i++) { await Exec(session, "INSERT INTO t DEFAULT VALUES"); } }
        await Exec(session, change);
        // Act
        await Exec(session, "ALTER TABLE t ALTER COLUMN id TYPE BIGINT");
        await Exec(session, "INSERT INTO t DEFAULT VALUES");
        // Assert
        (await Rows(session, "SELECT currval('t_id_seq'),lastval()"))[0].ShouldBe(new object?[] { expected, expected });
        (await Rows(session, "SELECT COUNT(*) FROM t"))[0][0].ShouldBe(called ? 4L : 1L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Backfill: account chunks and release on refusal")]
    [InlineData(1L, true)][InlineData(400000L, false)]
    public async Task ExecuteAsync_BackfillBudget_ShouldAccountAndKeepOldTableOnRefusal(long budget, bool refuses)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("backfill-budget-review", new() { StatementMemoryBudget = budget });
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(v TEXT)");
        await Exec(session, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Repeat("('" + new string('x', 1000) + "')", 130)));
        db.Catalog.TryGetTable("dbo", "t", out var original).ShouldBeTrue();
        var stages = new List<string>();
        SqlPlanExecutor.RewriteStage = (stage, _) => stages.Add(stage);
        try
        {
            // Act
            if (refuses)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(() => Exec(session, "ALTER TABLE t ADD id SERIAL"));
                failure.Diagnostics[0].Code.ShouldBe("CS05067");
            }
            else { await Exec(session, "ALTER TABLE t ADD id SERIAL"); }
            var metrics = session.LastStatementMetrics!;
            // Assert
            metrics.RecordsExamined.ShouldBeGreaterThan(0);
            metrics.HashMemoryBytes.ShouldBe(0);
            if (!refuses) { metrics.RecordsExamined.ShouldBe(130); metrics.PeakHashMemoryBytes.ShouldBeGreaterThan(130000); stages.Count(stage => stage == "Copied").ShouldBe(3); }
            stages.Contains("Published").ShouldBe(!refuses);
            db.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            table.Columns.Count.ShouldBe(refuses ? 1 : 2);
            if (refuses) { table.ShouldBeSameAs(original); }
            (await Rows(session, "SELECT COUNT(*) FROM t"))[0][0].ShouldBe(130L);
        }
        finally { SqlPlanExecutor.RewriteStage = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence visibility: failed backfill releases names")]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    [InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)]
    public async Task ExecuteAsync_AbandonedBackfill_ShouldReleaseSequenceName(int action)
    {
        // Arrange: the active reader pins abandoned owners until this test ends.
        await using var engine = SqlDatabaseEngine.Create("orphan-name-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await using var reader = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SCHEMA test");
        await Exec(session, "CREATE TABLE test.t(v INT)");
        await Exec(session, "INSERT INTO test.t VALUES(1)");
        await using var pin = await reader.BeginTransactionAsync(CancellationToken.None);
        int prepared = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) => { if (stage == "Prepared") { prepared++; throw new InvalidOperationException("Injected before publication."); } };
        try { await Should.ThrowAsync<InvalidOperationException>(() => Exec(session, "ALTER TABLE test.t ADD id SERIAL")); }
        finally { SqlPlanExecutor.RewriteStage = null; }
        prepared.ShouldBe(1);
        db.Catalog.FindSequence("test", "t_id_seq").ShouldBeNull();
        // Act
        if (action == 0) { await Exec(session, "ALTER TABLE test.t ADD id SERIAL"); }
        else if (action < 3) { await Exec(session, "CREATE SEQUENCE " + (action == 2 ? "IF NOT EXISTS " : "") + "test.t_id_seq"); }
        else if (action == 3) { await Exec(session, "DROP TABLE test.t"); await Exec(session, "DROP SCHEMA test RESTRICT"); }
        else if (action == 4) { await Exec(session, "CREATE TABLE test.t_id_seq(k INT)"); }
        else if (action == 5) { await Exec(session, "CREATE VIEW test.t_id_seq AS SELECT v FROM test.t"); }
        else if (action == 6) { await Exec(session, "CREATE TABLE test.t_id_seq AS SELECT v FROM test.t"); }
        else { await Exec(session, "ALTER TABLE test.t RENAME TO t_id_seq"); }
        // Assert
        if (action < 3) { db.Catalog.FindSequence("test", "t_id_seq").ShouldNotBeNull(); (await Rows(session, "SELECT nextval('test.t_id_seq')"))[0][0].ShouldBe(action == 0 ? 2L : 1L); }
        else if (action == 4) { db.Catalog.TryGetTable("test", "t_id_seq", out _).ShouldBeTrue(); }
        else if (action > 4) { (await Rows(session, "SELECT v FROM test.t_id_seq"))[0][0].ShouldBe(1); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence metadata: tiny identities expose eight bit precision")]
    public async Task ExecuteAsync_TinyIdentity_ShouldListCorrectPrecision()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("precision-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id TINYINT GENERATED BY DEFAULT AS IDENTITY)");
        // Act / Assert
        (await Rows(session, "SELECT SEQUENCE_NAME,NUMERIC_PRECISION FROM INFORMATION_SCHEMA.SEQUENCES"))[0].ShouldBe(new object?[] { "t_id_seq", 8L });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Truncate snapshot: exclude prepared and abandoned sequences")]
    public async Task CaptureTruncateReadSnapshot_HiddenSequences_ShouldFilterDirectory()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("snapshot-review", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await using var reader = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE TABLE t(id SERIAL)");
        await Exec(session, "CREATE TABLE u(v INT)");
        await Exec(session, "INSERT INTO u VALUES(1)");
        await using var pin = await reader.BeginTransactionAsync(CancellationToken.None);
        await Exec(session, "TRUNCATE t");
        int examined = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage != "Prepared") { return; }
            db.Catalog.CaptureTruncateReadSnapshot(pin.Context.Snapshot)!.Sequences.Select(sequence => sequence.Name).ShouldBe(new[] { "t_id_seq" });
            examined++;
            throw new InvalidOperationException("Injected before publication.");
        };
        // Act
        try { await Should.ThrowAsync<InvalidOperationException>(() => Exec(session, "ALTER TABLE u ADD id SERIAL")); }
        finally { SqlPlanExecutor.RewriteStage = null; }
        // Assert
        examined.ShouldBe(1);
        db.Catalog.CaptureTruncateReadSnapshot(pin.Context.Snapshot)!.Sequences.Select(sequence => sequence.Name).ShouldBe(new[] { "t_id_seq" });
    }

}
