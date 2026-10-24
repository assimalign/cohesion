using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// #1157: <c>ALTER TABLE ... ADD/DROP COLUMN</c> and <c>ADD/DROP CONSTRAINT</c> beside
/// concurrent INSERT writers used to end the process with an
/// <see cref="AccessViolationException"/>. Each ALTER rewrites the table's catalog record;
/// once the catalog page ran short of room, the slotted page relocated the grown record
/// past the end of the page buffer and overwrote the buffer pool's own objects.
/// </summary>
/// <remarks>
/// <para>
/// The stress cases run for two seconds each so the suite stays bounded in CI. For the
/// long local mode set <c>COHESION_SQL_STRESS_SECONDS</c> — for example to <c>60</c> —
/// and every case runs that long:
/// <code>
/// $env:COHESION_SQL_STRESS_SECONDS = 60
/// dotnet test resources/Database/Assimalign.Cohesion.Database.Sql/tests/Assimalign.Cohesion.Database.Sql.Tests.csproj --filter "FullyQualifiedName~SqlConcurrentDdlStressTests"
/// </code>
/// </para>
/// <para>
/// Two seconds is too short for an ordinary table to fill a catalog page, so the stress
/// table carries a padding CHECK that makes its catalog record about 4.4 KiB, more than
/// half a page body. A page can never hold two images of the record, so every successful
/// ADD, which relocates the whole grown record, meets a page with less than a record free
/// but more than the growth — the state the old relocation wrote past the page from — and
/// the catalog has to move the record to another page. Each case keeps going past its
/// duration until <see cref="minimumAdds"/> ADD statements have succeeded and a writer has
/// committed at least one row, so even a slow file-backed run crosses that state that many
/// times and a slow CI runner still proves inserts land; until a row commits, the DDL
/// pauses, and the assertion names the commonest INSERT failures. Writers keep arriving
/// throughout catch-up: the lock queue must admit waiting DDL ahead of new intent writers.
/// Each ALTER's elapsed time and each queued exclusive table-lock wait are recorded.
/// </para>
/// <para>
/// The deterministic cases need no concurrency at all: a few dozen ALTER cycles of an
/// ordinary table fill the catalog page, which is where the defect lived.
/// </para>
/// </remarks>
[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlConcurrentDdlStressTests : IDisposable
{
    private const string durationVariable = "COHESION_SQL_STRESS_SECONDS";
    private const int writerCount = 3;
    private const int sequentialCycles = 120;
    private const int minimumAdds = 3;
    private const int paddingLength = 4300;
    private static readonly TimeSpan _catchUpLimit = TimeSpan.FromMinutes(2);
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-ddl-stress", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public SqlConcurrentDdlStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
        var addFailures = new ConcurrentDictionary<string, int>();
        var insertFailures = new ConcurrentDictionary<string, int>();
        int alterCycles = 0;
        int addsSucceeded = 0;
        int positiveCommits = 0;
        int writerCommits = 0;
        var duration = StressDuration;
        var alterTimes = new List<double>();
        var waits = new ConcurrentBag<double>();
        var exclusiveTables = new ConcurrentDictionary<long, int>();
        var commitsDuringWait = new ConcurrentBag<int>();
        using var listener = new LockWaitListener("db");
        listener.Observed = written =>
        {
            var payload = written.Payload!;
            long sequence = (long)payload[1]!;
            if (written.EventName == "LockWaitStart" && Equals(payload[3], "Exclusive")
                && ((string)payload[2]!).StartsWith("Object:", StringComparison.Ordinal))
            {
                exclusiveTables[sequence] = Volatile.Read(ref writerCommits);
            }
            else if (written.EventName == "LockWaitStop" && exclusiveTables.TryRemove(sequence, out int before))
            {
                waits.Add((double)payload[3]!);
                commitsDuringWait.Add(Volatile.Read(ref writerCommits) - before);
            }
        };

        await using (var engine = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked)))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using (var setup = await database.CreateSessionAsync())
            {
                // The padding CHECK holds for every row the writers insert (qty is never
                // negative); it only makes the table's catalog record large.
                await setup.ExecuteAsync(
                    $"CREATE TABLE t (id INT, qty INT, CONSTRAINT pad CHECK (qty >= 0 OR '{new string('p', paddingLength)}' <> ''))");
            }

            using var deadline = new CancellationTokenSource(duration + _catchUpLimit);
            using var stop = new CancellationTokenSource();
            var elapsed = Stopwatch.StartNew();

            // Act: the #1157 reproducer — three writers and one ALTER session.
            var writers = Enumerable.Range(1, writerCount).Select(writer => DedicatedTestTask.Run(async () =>
            {
                var random = new Random(HashCode.Combine(seed, writer));
                await using var session = await database.CreateSessionAsync();
                for (int i = 1; !stop.IsCancellationRequested; i++)
                {
                    // Every 50th id is negative, so a CHECK (id > 0) backfill meets rows to refuse.
                    int id = (i % 50 == 0 ? -1 : 1) * ((writer * 10_000_000) + i);
                    attempted[id] = 0;
                    if (await TryExecuteAsync(session, $"INSERT INTO t (id, qty) VALUES ({id}, {Quantity(id)})", insertFailures, deadline.Token))
                    {
                        committed[id] = 0;
                        Interlocked.Increment(ref writerCommits);
                        if (id > 0)
                        {
                            Interlocked.Increment(ref positiveCommits);
                        }
                    }

                    if (random.Next(8) == 0)
                    {
                        await Task.Yield();
                    }
                }
            }));

            var alter = DedicatedTestTask.Run(async () =>
            {
                try
                {
                    var random = new Random(seed);
                    await using var session = await database.CreateSessionAsync();

                    // No writer hold-back: catch-up exercises the same queue as steady load.
                    async Task<bool> AlterAsync(string sql, ConcurrentDictionary<string, int>? failures = null)
                    {
                        long started = Stopwatch.GetTimestamp();
                        try
                        {
                            return await TryExecuteAsync(session, sql, failures, deadline.Token);
                        }
                        finally
                        {
                            if (sql.StartsWith("ALTER", StringComparison.Ordinal))
                            {
                                alterTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                            }
                        }
                    }

                    // Run for the duration, then on until enough ADDs have relocated the
                    // record and at least one writer has committed a row, within a hard
                    // limit. On a slow CI runner two seconds of DDL churn can pass before any
                    // insert lands; past the duration the DDL pauses until one does.
                    while (elapsed.Elapsed < duration
                        || ((Volatile.Read(ref addsSucceeded) < minimumAdds || Volatile.Read(ref positiveCommits) == 0 || waits.IsEmpty)
                            && elapsed.Elapsed < duration + _catchUpLimit))
                    {
                        if (elapsed.Elapsed >= duration && Volatile.Read(ref positiveCommits) == 0)
                        {
                            await Task.Delay(5);
                            continue;
                        }

                        int cycle = Interlocked.Increment(ref alterCycles);
                        if (ddl == "column" || (ddl == "mixed" && random.Next(2) == 0))
                        {
                            if (await AlterAsync($"ALTER TABLE t ADD COLUMN x{cycle} INT DEFAULT {cycle}", addFailures))
                            {
                                Interlocked.Increment(ref addsSucceeded);
                            }

                            await AlterAsync($"ALTER TABLE t DROP COLUMN x{cycle}");
                        }
                        else
                        {
                            // CHECK (id > 0) publishes only when no negative row slipped in
                            // after the DELETE, so under load it mostly exercises a refused
                            // backfill; CHECK (qty >= 0) holds for every row, so each cycle
                            // relocates the catalog record at least once.
                            await AlterAsync("DELETE FROM t WHERE id < 0");
                            if (await AlterAsync($"ALTER TABLE t ADD CONSTRAINT ck{cycle} CHECK (id > 0)"))
                            {
                                Interlocked.Increment(ref addsSucceeded);
                            }

                            await AlterAsync($"ALTER TABLE t DROP CONSTRAINT ck{cycle}");
                            if (await AlterAsync($"ALTER TABLE t ADD CONSTRAINT nn{cycle} CHECK (qty >= 0)", addFailures))
                            {
                                Interlocked.Increment(ref addsSucceeded);
                            }

                            await AlterAsync($"ALTER TABLE t DROP CONSTRAINT nn{cycle}");
                        }
                    }
                }
                finally
                {
                    stop.Cancel();
                }
            });

            await Task.WhenAll(writers.Append(alter));

            _output.WriteLine($"{ddl}, disk={fileBacked}: {alterTimes.Count} ALTERs, max {alterTimes.Max():F3} ms; "
                + $"{waits.Count} exclusive table waits, max {waits.DefaultIfEmpty().Max():F3} ms; {committed.Count} writer commits");

            // Assert: the DDL really ran — enough ADDs succeeded to fill catalog pages past
            // the old overflow point — and every committed row is intact.
            alterCycles.ShouldBeGreaterThan(0);
            waits.ShouldNotBeEmpty("the stress must exercise queued exclusive table locks under continuous writers");
            // Once X queues, only the three already-admitted writers can still finish.
            // Count work that passes DDL instead of imposing a machine-dependent deadline.
            commitsDuringWait.Max().ShouldBeLessThanOrEqualTo(writerCount,
                "new compatible writers must queue behind DDL; only in-flight writers may finish during its wait");
            addsSucceeded.ShouldBeGreaterThanOrEqualTo(minimumAdds,
                $"only {addsSucceeded} ADDs succeeded in {alterCycles} ALTER cycles; ADDs that every row satisfies failed with: "
                + string.Join("; ", addFailures.OrderByDescending(failure => failure.Value).Take(3).Select(failure => $"{failure.Value}x {failure.Key}")));
            committed.Keys.Count(id => id > 0).ShouldBeGreaterThan(0,
                $"no positive row committed in {elapsed.Elapsed}; INSERT failures: "
                + string.Join("; ", insertFailures.OrderByDescending(failure => failure.Value).Take(3).Select(failure => $"{failure.Value}x {failure.Key}")));
            await using var check = await database.CreateSessionAsync();
            await AssertRowsAsync(check, committed, attempted, ddl);
        }

        if (fileBacked)
        {
            await using var reopened = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked: true));
            var database = await reopened.OpenDatabaseAsync("db");
            await using var check = await database.CreateSessionAsync();
            await AssertRowsAsync(check, committed, attempted, ddl);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DDL stress: repeated ADD/DROP COLUMN outgrows the catalog page without corrupting it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTable_RepeatedAddDropColumn_ShouldKeepTheCatalogIntact(bool fileBacked)
    {
        // Arrange: no concurrency — each cycle grows the table's catalog record, relocates
        // it, and shrinks it again, which filled the catalog page within ~50 cycles.
        await using (var engine = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked)))
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
            await using var reopened = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked: true));
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
        await using (var engine = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked)))
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
            await SqlConstraintAssertions.ThrowAsync(async () =>
                await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (2, -1)"));
            (await RowsAsync(session, "SELECT id, qty FROM t"))
                .Select(Format).ShouldBe(["1,10"]);
        }

        if (fileBacked)
        {
            await using var reopened = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked: true));
            var database = await reopened.OpenDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await SqlConstraintAssertions.ThrowAsync(async () =>
                await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (2, -1)"));
            (await RowsAsync(session, "SELECT id, qty FROM t"))
                .Select(Format).ShouldBe(["1,10"]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DDL stress: ADD COLUMN past the catalog record size fails as a catalog error and leaves the table intact")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTable_AddColumnPastTheCatalogRecordSize_ShouldFailAndKeepTheTable(bool fileBacked)
    {
        // Arrange: every column with a long name adds about seventy bytes to the table's
        // catalog record, which must fit one slotted-page slot.
        string prefix = new('c', 60);
        int added = 0;
        DatabaseException? failure = null;

        await using (var engine = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked)))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t (id INT, qty INT)");
            await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (1, 10), (2, 20)");

            // Act
            for (int column = 1; column <= 400 && failure is null; column++)
            {
                try
                {
                    await session.ExecuteAsync($"ALTER TABLE t ADD COLUMN {prefix}{column} INT");
                    added++;
                }
                catch (DatabaseException exception)
                {
                    failure = exception;
                }
            }

            // Assert: a catalog error, not a storage failure, and the table is untouched; it
            // keeps accepting DDL that fits.
            AssertCatalogRecordTooLarge(failure, "table 'dbo.t'");
            added.ShouldBeGreaterThan(50);
            (await RowsAsync(session, "SELECT * FROM t ORDER BY id")).Select(row => row.Length).ShouldBe([2 + added, 2 + added]);
            await session.ExecuteAsync($"ALTER TABLE t DROP COLUMN {prefix}{added}");
            await session.ExecuteAsync("ALTER TABLE t ADD COLUMN small INT DEFAULT 3");
            (await RowsAsync(session, "SELECT id, qty, small FROM t ORDER BY id")).Select(Format).ShouldBe(["1,10,3", "2,20,3"]);
        }

        if (fileBacked)
        {
            await using var reopened = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked: true));
            var database = await reopened.OpenDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            (await RowsAsync(session, "SELECT * FROM t ORDER BY id")).Select(row => row.Length).ShouldBe([2 + added, 2 + added]);
            (await RowsAsync(session, "SELECT id, qty, small FROM t ORDER BY id")).Select(Format).ShouldBe(["1,10,3", "2,20,3"]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DDL stress: ADD CONSTRAINT past the catalog record size fails as a catalog error and leaves the table intact")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTable_AddConstraintPastTheCatalogRecordSize_ShouldFailAndKeepTheTable(bool fileBacked)
    {
        // Arrange
        string literal = new('k', SlottedPage.MaxRecordSize + 1024);

        await using (var engine = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked)))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t (id INT, qty INT)");
            await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (1, 10)");

            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync($"ALTER TABLE t ADD CONSTRAINT huge CHECK (qty > 0 OR '{literal}' <> '')"));

            // Assert: nothing was published — the huge CHECK is not enforced — and a
            // constraint that fits still can be added.
            AssertCatalogRecordTooLarge(failure, "table 'dbo.t'");
            await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (2, 0)");
            await session.ExecuteAsync("ALTER TABLE t ADD CONSTRAINT bounded CHECK (qty > -100)");
            await SqlConstraintAssertions.ThrowAsync(async () =>
                await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (3, -500)"));
            (await RowsAsync(session, "SELECT id, qty FROM t ORDER BY id")).Select(Format).ShouldBe(["1,10", "2,0"]);
        }

        if (fileBacked)
        {
            await using var reopened = SqlDatabaseEngine.Create("ddl-stress", NewOptions(fileBacked: true));
            var database = await reopened.OpenDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (4, -1)");
            await SqlConstraintAssertions.ThrowAsync(async () =>
                await session.ExecuteAsync("INSERT INTO t (id, qty) VALUES (5, -500)"));
            (await RowsAsync(session, "SELECT id, qty FROM t ORDER BY id")).Select(Format).ShouldBe(["1,10", "2,0", "4,-1"]);
        }
    }

    private static void AssertCatalogRecordTooLarge(DatabaseException? failure, string subject)
    {
        failure.ShouldNotBeNull();
        failure.ShouldContainMessage(subject, Case.Sensitive);
        failure.Message.ShouldContain("catalog record", Case.Sensitive);
        for (Exception? inner = failure; inner is not null; inner = inner.InnerException)
        {
            inner.ShouldNotBeAssignableTo<StorageException>();
        }
    }

    private SqlDatabaseEngineOptions NewOptions(bool fileBacked)
    {
        // Fast background workers put paced write-back and checkpoints in the race too.
        var options = new SqlDatabaseEngineOptions
        {
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
    /// <param name="session">The session to run the statement on.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="failures">When given, counts each lost race by message.</param>
    private static async Task<bool> TryExecuteAsync(SqlDatabaseSession session, string sql, ConcurrentDictionary<string, int>? failures = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await session.ExecuteAsync(sql, cancellationToken: cancellationToken);
            if (result is DatabaseResultSet set)
            {
                await set.DisposeAsync();
            }

            return true;
        }
        catch (DatabaseException exception)
        {
            failures?.AddOrUpdate($"{exception.GetType().Name}: {exception.Message}", 1, (_, count) => count + 1);
            for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
            {
                inner.ShouldNotBeAssignableTo<StorageException>();
            }

            return false;
        }
    }

    private static async Task AssertRowsAsync(
        SqlDatabaseSession session,
        ConcurrentDictionary<int, byte> committed,
        ConcurrentDictionary<int, byte> attempted,
        string ddl)
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

        // In the constraint and mixed modes the ALTER session deletes negative rows, so
        // those may legitimately be gone; column mode deletes nothing, so every committed
        // row must read back there.
        var missing = committed.Keys.Where(id => (id > 0 || ddl == "column") && !seen.Contains(id)).Take(5).ToList();
        missing.ShouldBeEmpty("committed rows missing");
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        var result = (await session.ExecuteAsync(sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result!.GetRowsAsync())
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }

        return rows;
    }
}
