using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - A returned spilled SELECT keeps delivering while a held checkpoint pauses writes (#1283, #1789)")]
    public async Task GetRowsAsync_JournalAdmissionPaused_ShouldContinueSpilledDelivery()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Create("spill-delivery-pause", 262144);
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await Seed(session);
        await using var result = (await session.ExecuteAsync("SELECT id,k FROM t ORDER BY id", cancellationToken: guard.Token)).ShouldBeOfType<SqlSpilledResultSet>();
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        database.Coordinator.Checkpoint(guard.Token);
        database.DataStorage.CheckpointJournalSize = 1; gate.JournalSizeLimit = 2;
        using var holder = database.DataStorage.BeginTransaction("HeldSpillDeliveryCheckpoint");
        gate.ObserveAdmissionPause().ShouldBeTrue();
        // Act
        var rows = await Rows(result, guard.Token);
        // Assert: delivery neither opens admission nor requires the blocked checkpoint.
        gate.IsAdmissionPaused.ShouldBeTrue();
        rows.Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 4000).Cast<object?>());
        gate.JournalSizeLimit = 0; holder.Rollback(); database.Coordinator.Checkpoint(guard.Token);
        gate.IsAdmissionPaused.ShouldBeFalse();
        (await Rows(result, guard.Token)).ShouldBe(rows);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL qualified-star CTAS preserves rows and catalog through source spill (#1789, #1792)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NaturalStarTableAs_ShouldMatchFittingSource(bool distinct)
    {
        // Arrange: many duplicate wide rows spill the sort, while DISTINCT's seen set fits.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var fitting = await Create("natural-ctas-fitting", 67108864);
        await using var spilling = await Create("natural-ctas-spilling", 262144);
        var databases = new[] { await fitting.CreateDatabaseAsync("db", guard.Token), await spilling.CreateDatabaseAsync("db", guard.Token) };
        RowValues[]? expected = null;
        (string, object, bool, object?, object?)[]? columns = null;
        foreach (var database in databases)
        {
            await using var session = await database.CreateSessionAsync(guard.Token);
            await SeedJoinedSpillAsync(session, guard.Token);
            // Act
            var result = await session.ExecuteAsync("CREATE TABLE archive AS SELECT " + (distinct ? "DISTINCT " : "") +
                "t.* FROM t NATURAL JOIN u ORDER BY id", cancellationToken: guard.Token);
            // Assert
            result.AffectedCount.ShouldBe(distinct ? 20 : 2000);
            if (database == databases[1]) { SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0); }
            else { SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull(); }
            var table = database.Catalog.Tables.Single(item => item.Name == "archive");
            var shape = table.Columns.Select(c => (c.Name, (object)c.Type, c.IsNullable, (object?)c.Collation, (object?)c.DefaultLiteral)).ToArray();
            if (columns is null) { columns = shape; } else { shape.ShouldBe(columns); }
            table.Columns.Select(c => c.Name).ShouldBe(["id", "note"]);
            table.PrimaryKeyColumns.ShouldBeEmpty(); table.Constraints.ShouldBeEmpty();
            database.Catalog.GetIndexes(table.ObjectId).ShouldBeEmpty();
            await using var copied = (DatabaseResultSet)await session.ExecuteAsync("SELECT * FROM archive ORDER BY id", cancellationToken: guard.Token);
            var rows = await Rows(copied, guard.Token);
            if (expected is null) { expected = rows; } else { rows.ShouldBe(expected); }
        }
        TempBytes(spilling.TempSpace!).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL and outer qualified-star root sorts preserve nullable keys and discarded aliases through spill (#1789, #1792)")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ExecuteAsync_JoinedStarSort_ShouldMatchFittingKeys(bool outer, bool distinct, bool starOnOuter)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var fitting = await Create("joined-fitting", 67108864);
        await using var spilling = await Create("joined-spilling", 262144);
        await using var a = await (await fitting.CreateDatabaseAsync("db", guard.Token)).CreateSessionAsync(guard.Token);
        await using var b = await (await spilling.CreateDatabaseAsync("db", guard.Token)).CreateSessionAsync(guard.Token);
        await SeedJoinedSpillAsync(a, guard.Token); await SeedJoinedSpillAsync(b, guard.Token);
        string sql = "SELECT " + (distinct ? "DISTINCT " : "") + "t.* AS ignored" +
            (starOnOuter ? " FROM u LEFT JOIN t USING(id) ORDER BY t.id NULLS FIRST" : outer
                ? ",u.rank AS nullable_key FROM t LEFT JOIN u USING(id) ORDER BY nullable_key NULLS FIRST,t.id"
                : " FROM t NATURAL JOIN u ORDER BY id");
        // Act
        await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql, cancellationToken: guard.Token);
        await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql, cancellationToken: guard.Token);
        // Assert
        right.ShouldBeOfType<SqlSpilledResultSet>(); left.ShouldBeOfType<SqlMaterializedResultSet>();
        (await Rows(right, guard.Token)).ShouldBe(await Rows(left, guard.Token));
        right.Columns.Select(c => (c.Name, c.Type, c.IsNullable)).ShouldBe(left.Columns.Select(c => (c.Name, c.Type, c.IsNullable)));
        right.Columns.ShouldNotContain(c => c.Name == "ignored");
        if (starOnOuter)
        {
            right.Columns.ShouldAllBe(c => c.IsNullable);
            (await Rows(right, guard.Token)).ShouldContain(row => row.Cells[0] == null);
        }
        else if (outer) { right.Columns[^1].IsNullable.ShouldBeTrue(); }
        // A discarded alias never becomes an ORDER BY key, at either grant.
        foreach (var session in new[] { a, b })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "SELECT t.* AS ignored FROM t NATURAL JOIN u ORDER BY ignored", cancellationToken: guard.Token));
            failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS target boundary distinguishes catalog width from target admission (#1792)")]
    [InlineData(1664, "CS05042")]
    [InlineData(1665, "CS05097")]
    public async Task ExecuteAsync_TableAsTargetBoundary_ShouldRefuseBeforePublication(int count, string code)
    {
        // Arrange: a legal target list can still exceed the one-record catalog width.
        await using var engine = await Create("ctas-target-boundary", 67108864);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string targets = string.Join(',', Enumerable.Range(0, count).Select(i => "1 AS c" + i));
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "CREATE TABLE archive AS SELECT " + targets, cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == code);
        database.Catalog.Tables.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS unnamed output keeps its explicit-name refusal through a sorted source (#1789, #1792)")]
    public async Task ExecuteAsync_TableAsUnnamedSortedOutput_ShouldKeepNameRefusal()
    {
        // Arrange
        await using var engine = await Create("ctas-unnamed", 262144);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedJoinedSpillAsync(session, CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "CREATE TABLE archive AS SELECT t.id+1 FROM t NATURAL JOIN u ORDER BY t.id", cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS03068");
        database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        await using var source = (DatabaseResultSet)await session.ExecuteAsync(
            "SELECT t.id+1 FROM t NATURAL JOIN u ORDER BY t.id", cancellationToken: CancellationToken.None);
        source.Columns.ShouldHaveSingleItem().Name.ShouldBe("column1");
    }

    private static async Task SeedJoinedSpillAsync(SqlDatabaseSession session, CancellationToken token)
    {
        await session.ExecuteAsync("CREATE TABLE t(id INT NOT NULL,note TEXT COLLATE binary)", cancellationToken: token);
        await session.ExecuteAsync("CREATE TABLE u(id INT NOT NULL,rank INT NOT NULL)", cancellationToken: token);
        await session.ExecuteAsync("INSERT INTO u VALUES" + string.Join(',', Enumerable.Range(0, 20).Select(i => $"({i},{19 - i})")) + ",(100,100)", cancellationToken: token);
        for (int first = 0; first < 4000; first += 100)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 100).Select(i => $"({i % 40},'{new string('x', 512)}')")), cancellationToken: token);
        }
    }
}
