using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// #1157: <c>ALTER TABLE ... ADD/DROP COLUMN</c> and <c>ADD/DROP CONSTRAINT</c> beside
/// concurrent INSERT writers used to end the process with an
/// <see cref="AccessViolationException"/>. Each ALTER rewrites the table's catalog record;
/// once the catalog page ran short of room, the slotted page relocated the grown record
/// past the end of the page buffer and overwrote the buffer pool's own objects.
/// </summary>
/// <remarks>
/// The stress cases run for two seconds each so the suite stays bounded in CI. For the
/// long local mode set <c>COHESION_SQL_STRESS_SECONDS</c> — for example to <c>60</c> —
/// and every case runs that long:
/// <code>
/// $env:COHESION_SQL_STRESS_SECONDS = 60
/// dotnet test resources/Database/Assimalign.Cohesion.Database.Sql/tests/Assimalign.Cohesion.Database.Sql.Tests.csproj --filter "FullyQualifiedName~SqlConcurrentDdlStressTests"
/// </code>
/// The deterministic cases need no concurrency at all: a few dozen ALTER cycles fill the
/// catalog page, which is where the defect lived.
/// </remarks>
public sealed class SqlConcurrentDdlStressTests : IDisposable
{
    private const string durationVariable = "COHESION_SQL_STRESS_SECONDS";
    private const int writerCount = 3;
    private const int sequentialCycles = 120;
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-ddl-stress", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }

    private static TimeSpan StressDuration
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(durationVariable);
            return int.TryParse(value, out int seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(2);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DDL stress: concurrent ALTER TABLE and INSERT keep the process up and every committed row intact")]
    [InlineData("column", false, 1)]
    [InlineData("column", true, 2)]
    [InlineData("constraint", false, 3)]
    [InlineData("constraint", true, 4)]
    [InlineData("mixed", false, 5)]
    [InlineData("mixed", true, 6)]
    public async Task AlterTable_ConcurrentWithInserts_ShouldKeepEveryCommittedRow(string ddl, bool fileBacked, int seed)
    {
        // Arrange
        var committed = new ConcurrentDictionary<int, byte>();
        var attempted = new ConcurrentDictionary<int, byte>();
        int alterCycles = 0;

        await using (var engine = SqlDatabaseEngine.Create(NewOptions(fileBacked)))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using (var setup = await database.CreateSessionAsync())
            {
                await setup.ExecuteAsync("CREATE TABLE t (id INT, qty INT)");
            }

            using var stop = new CancellationTokenSource(StressDuration);

            // Act: the #1157 reproducer — three writers and one ALTER session.
            var writers = Enumerable.Range(1, writerCount).Select(writer => Task.Run(async () =>
            {
                var random = new Random(HashCode.Combine(seed, writer));
                await using var session = await database.CreateSessionAsync();
                for (int i = 1; !stop.IsCancellationRequested; i++)
                {
                    // Every 50th id is negative, so a CHECK (id > 0) backfill meets rows to refuse.
                    int id = (i % 50 == 0 ? -1 : 1) * ((writer * 10_000_000) + i);
                    attempted[id] = 0;
                    if (await TryExecuteAsync(session, $"INSERT INTO t (id, qty) VALUES ({id}, {Quantity(id)})"))
                    {
                        committed[id] = 0;
                    }

                    if (random.Next(8) == 0)
                    {
                        await Task.Yield();
                    }
                }
            }));

            var alter = Task.Run(async () =>
            {
                var random = new Random(seed);
                await using var session = await database.CreateSessionAsync();
                while (!stop.IsCancellationRequested)
                {
                    int cycle = Interlocked.Increment(ref alterCycles);
                    if (ddl == "column" || (ddl == "mixed" && random.Next(2) == 0))
                    {
                        await TryExecuteAsync(session, $"ALTER TABLE t ADD COLUMN x{cycle} INT DEFAULT {cycle}");
                        await TryExecuteAsync(session, $"ALTER TABLE t DROP COLUMN x{cycle}");
                    }
                    else
                    {
                        await TryExecuteAsync(session, "DELETE FROM t WHERE id < 0");
                        await TryExecuteAsync(session, $"ALTER TABLE t ADD CONSTRAINT ck{cycle} CHECK (id > 0)");
                        await TryExecuteAsync(session, $"ALTER TABLE t DROP CONSTRAINT ck{cycle}");
                    }
                }
            });

            await Task.WhenAll(writers.Append(alter));

            // Assert
            alterCycles.ShouldBeGreaterThan(0);
            committed.Keys.Count(id => id > 0).ShouldBeGreaterThan(0);
            await using var check = await database.CreateSessionAsync();
            await AssertRowsAsync(check, committed, attempted);
        }

        if (fileBacked)
        {
            await using var reopened = SqlDatabaseEngine.Create(NewOptions(fileBacked: true));
            var database = await reopened.OpenDatabaseAsync("db");
            await using var check = await database.CreateSessionAsync();
            await AssertRowsAsync(check, committed, attempted);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DDL stress: repeated ADD/DROP COLUMN outgrows the catalog page without corrupting it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTable_RepeatedAddDropColumn_ShouldKeepTheCatalogIntact(bool fileBacked)
    {
        // Arrange: no concurrency — each cycle grows the table's catalog record, relocates
        // it, and shrinks it again, which filled the catalog page within ~50 cycles.
        await using (var engine = SqlDatabaseEngine.Create(NewOptions(fileBacked)))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t (id INT, qty INT)");
            await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (1, 10), (2, 20)");

            // Act
            for (int cycle = 1; cycle <= sequentialCycles; cycle++)
            {
                await session.ExecuteAsync($"ALTER TABLE t ADD COLUMN x{cycle} INT DEFAULT {cycle}");
                await session.ExecuteAsync($"ALTER TABLE t DROP COLUMN x{cycle}");
            }

            await session.ExecuteAsync("ALTER TABLE t ADD COLUMN last INT DEFAULT 7");

            // Assert
            (await RowsAsync(session, "SELECT id, qty, last FROM t ORDER BY id"))
                .Select(Format).ShouldBe(["1,10,7", "2,20,7"]);
        }

        if (fileBacked)
        {
            await using var reopened = SqlDatabaseEngine.Create(NewOptions(fileBacked: true));
            var database = await reopened.OpenDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            (await RowsAsync(session, "SELECT * FROM t ORDER BY id"))
                .Select(Format).ShouldBe(["1,10,7", "2,20,7"]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DDL stress: repeated ADD/DROP CONSTRAINT outgrows the catalog page without corrupting it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTable_RepeatedAddDropConstraint_ShouldKeepTheCatalogIntact(bool fileBacked)
    {
        // Arrange
        await using (var engine = SqlDatabaseEngine.Create(NewOptions(fileBacked)))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t (id INT, qty INT)");
            await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (1, 10)");

            // Act
            for (int cycle = 1; cycle <= sequentialCycles; cycle++)
            {
                await session.ExecuteAsync($"ALTER TABLE t ADD CONSTRAINT positive_{cycle} CHECK (qty > 0)");
                await session.ExecuteAsync($"ALTER TABLE t DROP CONSTRAINT positive_{cycle}");
            }

            await session.ExecuteAsync("ALTER TABLE t ADD CONSTRAINT positive CHECK (qty > 0)");

            // Assert: the surviving constraint is enforced and the row is intact.
            await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (2, -1)"));
            (await RowsAsync(session, "SELECT id, qty FROM t"))
                .Select(Format).ShouldBe(["1,10"]);
        }

        if (fileBacked)
        {
            await using var reopened = SqlDatabaseEngine.Create(NewOptions(fileBacked: true));
            var database = await reopened.OpenDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (2, -1)"));
            (await RowsAsync(session, "SELECT id, qty FROM t"))
                .Select(Format).ShouldBe(["1,10"]);
        }
    }

    private SqlDatabaseEngineOptions NewOptions(bool fileBacked)
    {
        // Fast background workers put paced write-back and checkpoints in the race too.
        var options = new SqlDatabaseEngineOptions
        {
            EngineName = "ddl-stress",
            PageWriteBackInterval = TimeSpan.FromMilliseconds(5),
            CheckpointInterval = TimeSpan.FromMilliseconds(50),
        };

        if (fileBacked)
        {
            options.RootPath = _rootPath;
        }

        return options;
    }

    private static int Quantity(int id) => Math.Abs(id % 997);

    private static string Format(object?[] row) => string.Join(",", row);

    /// <summary>
    /// Runs a statement that may lose a race. Contention surfaces as
    /// <see cref="DatabaseException"/> (a table changed while the statement waited, a CHECK
    /// a concurrent row refuses); a storage failure is never contention and fails the test.
    /// </summary>
    private static async Task<bool> TryExecuteAsync(IDatabaseSession session, string sql)
    {
        try
        {
            var result = await session.ExecuteAsync(sql);
            if (result is QueryResultSet set)
            {
                await set.DisposeAsync();
            }

            return true;
        }
        catch (DatabaseException exception)
        {
            for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
            {
                inner.ShouldNotBeAssignableTo<StorageException>();
            }

            return false;
        }
    }

    private static async Task AssertRowsAsync(
        IDatabaseSession session,
        ConcurrentDictionary<int, byte> committed,
        ConcurrentDictionary<int, byte> attempted)
    {
        var rows = await RowsAsync(session, "SELECT id, qty FROM t");
        var seen = new HashSet<int>();

        foreach (var row in rows)
        {
            int id = Convert.ToInt32(row[0]);
            seen.Add(id).ShouldBeTrue($"row {id} is stored twice");
            attempted.ContainsKey(id).ShouldBeTrue($"row {id} was never inserted");
            Convert.ToInt32(row[1]).ShouldBe(Quantity(id), $"row {id} quantity");
        }

        // Negative rows may legitimately be gone (the ALTER session deletes them); every
        // other committed row must read back.
        var missing = committed.Keys.Where(id => id > 0 && !seen.Contains(id)).Take(5).ToList();
        missing.ShouldBeEmpty("committed rows missing");
    }

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string sql)
    {
        var result = (await session.ExecuteAsync(sql)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result!.GetRowsAsync())
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }

        return rows;
    }
}
