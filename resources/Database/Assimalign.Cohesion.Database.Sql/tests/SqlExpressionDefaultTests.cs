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

public sealed class SqlExpressionDefaultTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - DEFAULT: immutable expressions evaluate once per declaration")]
    public async Task Declare_ImmutableDefault_ShouldRetainValidatedBinding()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("immutable-default");
        builder.Functions.Add(SqlScalarFunction.Create("tracked", (int value) => { calls++; return value; }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        await session.ExecuteAsync("CREATE TABLE t (id INT, n INT DEFAULT tracked(1) + 2)", cancellationToken: CancellationToken.None);
        calls.ShouldBe(1);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2)", cancellationToken: CancellationToken.None);
        calls.ShouldBe(1);
        await session.ExecuteAsync("ALTER TABLE t ADD extra INT DEFAULT tracked(2) + 2", cancellationToken: CancellationToken.None);
        calls.ShouldBe(2);
        await session.ExecuteAsync("ALTER TABLE t ALTER n SET DEFAULT tracked(3) + 2", cancellationToken: CancellationToken.None);
        calls.ShouldBe(3);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (3)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT n, extra FROM t ORDER BY id")).Select(row => (row[0], row[1]))
            .ShouldBe(new (object?, object?)[] { (3, 4), (3, 4), (5, 4) });
        calls.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DEFAULT: row evaluation shares the transaction clock and generates distinct UUIDs")]
    public async Task Insert_ExpressionDefaults_ShouldEvaluatePerRowWithTransactionClock()
    {
        // Arrange
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        await using var engine = SqlDatabaseEngine.Create("expression-defaults", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, n INT DEFAULT 1 + 2, label TEXT DEFAULT UPPER('x'), c INT DEFAULT CAST('1' AS INT), stamp TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP, wall TIMESTAMP DEFAULT CURRENT_TIMESTAMP, day DATE DEFAULT CURRENT_DATE, tm TIME DEFAULT LOCALTIME, uid UUID DEFAULT GEN_RANDOM_UUID());", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        var begin = clock.Instant;
        clock.Instant += TimeSpan.FromHours(1);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2)", cancellationToken: CancellationToken.None);
        clock.Instant += TimeSpan.FromHours(1);
        await session.ExecuteAsync("INSERT INTO t (id) SELECT id + 2 FROM t", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("UPDATE t SET stamp = DEFAULT WHERE id = 1", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);

        // Assert
        var rows = await Rows(session, "SELECT n, label, c, stamp, wall, day, tm, uid FROM t ORDER BY id");
        rows.Count.ShouldBe(4);
        rows.ShouldAllBe(row => Equals(row[0], 3) && Equals(row[1], "X") && Equals(row[2], 1) && Equals(row[3], begin)
            && Equals(row[4], DateTime.SpecifyKind(begin.UtcDateTime, DateTimeKind.Unspecified))
            && Equals(row[5], DateOnly.FromDateTime(begin.UtcDateTime)) && Equals(row[6], TimeOnly.FromDateTime(begin.UtcDateTime)));
        rows.Select(row => row[7]).Distinct().Count().ShouldBe(4);
        await session.ExecuteAsync("INSERT INTO t (id, stamp) VALUES (5, DEFAULT)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT stamp FROM t WHERE id = 5")).Single()[0].ShouldBe(clock.Instant);
        clock.Instant += TimeSpan.FromHours(1);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (6)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT stamp FROM t WHERE id = 6")).Single()[0].ShouldBe(clock.Instant);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ADD COLUMN: stable missing values freeze across SET DEFAULT and restart")]
    public async Task AddColumn_StableDefault_ShouldFreezeMissingValueAcrossReopen()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-expression-defaults", Guid.NewGuid().ToString("N"));
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        var frozen = clock.Instant;
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("fast-default", new() { RootPath = root, TimeProvider = clock }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("ALTER TABLE t ADD stamp TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP NOT NULL", cancellationToken: CancellationToken.None);
                clock.Instant += TimeSpan.FromDays(1);
                await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)", cancellationToken: CancellationToken.None);
                (await Rows(session, "SELECT stamp FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { frozen, clock.Instant });
                await session.ExecuteAsync("ALTER TABLE t ALTER stamp SET DEFAULT NOW()", cancellationToken: CancellationToken.None);
            }
            await using var reopened = SqlDatabaseEngine.Create("fast-default-reopen", new() { RootPath = root, TimeProvider = clock });
            var opened = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var second = await opened.CreateSessionAsync(CancellationToken.None);

            // Act / Assert
            clock.Instant += TimeSpan.FromDays(1);
            await second.ExecuteAsync("INSERT INTO t (id) VALUES (3)", cancellationToken: CancellationToken.None);
            var rows = await Rows(second, "SELECT stamp FROM t ORDER BY id");
            rows[0][0].ShouldBe(frozen);
            rows[2][0].ShouldBe(clock.Instant);
            (await Rows(second, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't' AND COLUMN_NAME = 'stamp'")).Single()[0].ShouldBe("NOW()");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ADD COLUMN: volatile defaults require an empty table")]
    public async Task AddColumn_VolatileDefault_ShouldRejectLiveRowsBeforeMutation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("volatile-add", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("ALTER TABLE t ADD uid UUID DEFAULT GEN_RANDOM_UUID()", cancellationToken: CancellationToken.None).AsTask());
        error.Diagnostics.Single().Code.ShouldBe("COHSQLE027");
        error.Message.ShouldContain("uid", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Count.ShouldBe(1);
        await session.ExecuteAsync("DELETE FROM t", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER TABLE t ADD uid UUID DEFAULT GEN_RANDOM_UUID()", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (2), (3)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT uid FROM t")).Select(row => row[0]).Distinct().Count().ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DEFAULT: invalid variable-free definitions fail before publication")]
    [InlineData("id + 1")]
    [InlineData("@p")]
    [InlineData("SUM(1)")]
    [InlineData("-'x'")]
    [InlineData("(1 / 0)")]
    [InlineData("CAST('x' AS INT)")]
    [InlineData("UPPER('x')")]
    public async Task Create_InvalidDefault_ShouldRejectBeforeMutation(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"CREATE TABLE t (id INT, bad INT DEFAULT {expression})", cancellationToken: CancellationToken.None).AsTask());
        failure.Message.ShouldContain("bad", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "t", out _).ShouldBeFalse();
    }

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}
