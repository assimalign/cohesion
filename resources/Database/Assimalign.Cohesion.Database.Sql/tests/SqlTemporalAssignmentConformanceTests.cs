using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTemporalAssignmentConformanceTests
{
    public static IEnumerable<object[]> Inputs() =>
    [
        ["INTERVAL", "12", TimeSpan.FromSeconds(12)],
        ["INTERVAL", " -12.0000001 ", TimeSpan.FromSeconds(-12).Add(TimeSpan.FromTicks(-1))],
        ["INTERVAL", "2 days 03:04:05", new TimeSpan(2, 3, 4, 5)],
        ["INTERVAL", "P1DT2H", TimeSpan.FromHours(26)],
        ["INTERVAL", "1 2 hours", TimeSpan.FromHours(26)],
        ["INTERVAL", "@ 1 day 2 hours ago", TimeSpan.FromHours(-26)],
        ["INTERVAL", "-1 02:03:04", TimeSpan.FromSeconds(-79016)],
        ["interval_domain", "12.5", TimeSpan.FromMilliseconds(12500)],
        ["TIME", " 13:45 ", new TimeOnly(13, 45)],
        ["TIME", "00:00:00.0000001", new TimeOnly(1)],
        ["TIME", "1:02", new TimeOnly(1, 2)],
        ["TIME", "1 PM", new TimeOnly(13, 0)],
        ["TIME", "12:02 am", new TimeOnly(0, 2)],
        ["TIME", "13:45:00.", new TimeOnly(13, 45)],
        ["TIME", "10:00Z", new TimeOnly(10, 0)],
        ["TIME", "10:00+05:30", new TimeOnly(10, 0)],
        ["TIME", "10:00 PST", new TimeOnly(10, 0)],
        ["REAL", "1e3", 1000f],
        ["DOUBLE", "-0.125", -0.125d],
        ["time_domain", "23:59:59.1234567", new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(1234567))],
    ];

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Conformance: TIME and INTERVAL assignments share CAST input (#1422)")]
    [MemberData(nameof(Inputs))]
    public async Task ExecuteAsync_TemporalTextAssignment_ShouldMatchCastOnEveryWritePath(string type, string text, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("temporal-assignment", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN interval_domain AS INTERVAL;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN time_domain AS TIME;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT, v {type} DEFAULT '{text}');", cancellationToken: CancellationToken.None);

        // Act
        foreach (string sql in new[]
        {
            "INSERT INTO t (id) VALUES (1);",
            $"INSERT INTO t VALUES (2, '{text}');",
            $"INSERT INTO t SELECT 3, '{text}';",
            "INSERT INTO t VALUES (4, NULL);",
            $"UPDATE t SET v = '{text}' WHERE id = 4;",
            $"ALTER TABLE t ADD COLUMN extra {type} DEFAULT '{text}';",
        })
        {
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        }
        await using var result = (await session.ExecuteAsync($"SELECT v, extra, CAST('{text}' AS {type}) FROM t ORDER BY id;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.GetValue(0).ShouldBe(expected);
            row.GetValue(1).ShouldBe(expected);
            row.GetValue(2).ShouldBe(expected);
            count++;
        }
        count.ShouldBe(4);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Conformance: invalid temporal assignment fails atomically (#1422)")]
    [InlineData("INTERVAL", "1.02:03:04", "CS02047")]
    [InlineData("INTERVAL", "2.03:04:05.1234567", "CS02047")]
    [InlineData("INTERVAL", "-2.03:04:05.1234567", "CS02047")]
    [InlineData("INTERVAL", "1 year", "CS02047")]
    [InlineData("INTERVAL", "0.00000001", "CS02047")]
    [InlineData("INTERVAL", "garbage", "CS02047")]
    [InlineData("INTERVAL", "12\u00a0", "CS02047")]
    [InlineData("INTERVAL", "1\u00a02 hours", "CS02047")]
    [InlineData("INTERVAL", "99999999999999999999999999999 days", "CS02062")]
    [InlineData("TIME", "24:00", "CS02048")]
    [InlineData("TIME", "25:00", "CS02048")]
    [InlineData("TIME", "10:00:00.12345678", "CS02047")]
    [InlineData("TIME", "10:00\u00a0", "CS02047")]
    [InlineData("REAL", "1,000", "CS02022")]
    [InlineData("REAL", "1e400", "CS02002")]
    [InlineData("DOUBLE", "1,000", "CS02022")]
    [InlineData("DOUBLE", "1e400", "CS02002")]
    public async Task ExecuteAsync_InvalidTemporalText_ShouldRejectCastAndAssignment(string type, string text, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-temporal-assignment", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (v {type});", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE u (v {type});", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO u VALUES (NULL);", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string sql in new[]
        {
            $"INSERT INTO t VALUES (NULL), ('{text}');",
            $"INSERT INTO t SELECT '{text}';",
            $"UPDATE u SET v = '{text}';",
            $"CREATE TABLE bad_default (v {type} DEFAULT '{text}');",
            $"ALTER TABLE t ADD COLUMN extra {type} DEFAULT '{text}';",
            $"SELECT CAST('{text}' AS {type});",
        })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code, sql);
        }
        await using var result = (await session.ExecuteAsync("SELECT * FROM t;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
        count.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Format 25 temporal defaults are refused before rebinding (#1422)")]
    [InlineData("INTERVAL", "12")]
    [InlineData("INTERVAL", "1.02:03:04")]
    [InlineData("INTERVAL", "2.03:04:05.1234567")]
    [InlineData("INTERVAL", "-2.03:04:05.1234567")]
    [InlineData("TIME", "1:02")]
    [InlineData("TIME", "1 PM")]
    public async Task Open_Format25Default_ShouldRefuseBeforeReadingOldText(string type, string text)
    {
        // Arrange: publish base-compatible catalog text directly, including an ADD COLUMN missing value.
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("old-default", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            var column = new SqlCatalogColumn("v", new DatabaseTypeInfo(type == "TIME" ? DatabaseType.Time : DatabaseType.TimeSpan),
                defaultLiteral: $"'{text}'", missingValueLiteral: $"'{text}'");
            await database.Catalog.PublishTableAsync(new SqlCatalogTable(table.ObjectId, table.Schema, table.Name,
                [table.Columns[0], column], table.PrimaryKeyColumns, table.Owner, table.OwningSchema, table.Constraints, table.DroppedColumnOrdinals),
                [], [], replaceExisting: true, cancellationToken: CancellationToken.None);
            await database.Catalog.SetRecordSpaceFormatVersionAsync(25, CancellationToken.None);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using var reopening = SqlDatabaseEngine.Create("old-default", new() { StorageStrategy = storage });
        var error = await Should.ThrowAsync<SqlDataStorageFormatException>(async () =>
            await reopening.OpenDatabaseAsync("db", CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06041");
        error.Message.ShouldContain("uses data-storage format 25");
        error.Message.ShouldContain("supports only format 33");
    }

    public static IEnumerable<object[]> RenderedDefaults() =>
    [
        [TimeSpan.MinValue], [TimeSpan.MaxValue], [TimeSpan.FromTicks(1)], [TimeSpan.Zero],
        [-new TimeSpan(2, 3, 4, 5).Add(TimeSpan.FromTicks(1234567))],
        [TimeSpan.FromDays(-2).Add(TimeSpan.FromHours(3))],
        [new TimeOnly(13, 45, 30).Add(TimeSpan.FromTicks(1234567))],
    ];

    [Theory(DisplayName = "Cohesion Test [Sql] - Rendered temporal defaults execute and reopen exactly (#1422)")]
    [MemberData(nameof(RenderedDefaults))]
    public async Task Open_RenderedDefault_ShouldPreserveEveryTick(object expected)
    {
        // Arrange
        var schema = expected is TimeSpan interval
            ? SqlSchema.Compile("db", builder => builder.Table<IntervalRow>("t", table => table.Default(row => row.Value, interval)))
            : SqlSchema.Compile("db", builder => builder.Table<TimeRow>("t", table => table.Default(row => row.Value, (TimeOnly)expected)));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("round-trip-default", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync($"CREATE TABLE t (v {(expected is TimeSpan ? "INTERVAL" : "TIME")} DEFAULT {schema.Tables[0].Columns[0].DefaultLiteral});", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO t DEFAULT VALUES;", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"ALTER TABLE t ADD COLUMN frozen {(expected is TimeSpan ? "INTERVAL" : "TIME")} DEFAULT {schema.Tables[0].Columns[0].DefaultLiteral};", cancellationToken: CancellationToken.None);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using var reopening = SqlDatabaseEngine.Create("round-trip-default", new() { StorageStrategy = storage });
        var reopened = await reopening.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await reopened.CreateSessionAsync(CancellationToken.None);
        await reader.ExecuteAsync("INSERT INTO t DEFAULT VALUES;", cancellationToken: CancellationToken.None);
        await using var result = (await reader.ExecuteAsync("SELECT * FROM t;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.GetValue(0).ShouldBe(expected);
            row.GetValue(1).ShouldBe(expected);
            count++;
        }
        count.ShouldBe(2);
    }

    private sealed record IntervalRow(TimeSpan Value);
    private sealed record TimeRow(TimeOnly Value);
}
