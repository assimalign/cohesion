using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;

/// <summary>
/// A multi-row INSERT that fails on a UNIQUE index rolls its statement bracket back, and the
/// rollback restores every page the statement changed in place: the index pages its entries and
/// splits changed, and the table's heap page its rows went to, which also holds committed rows.
/// Until #1371 the restore cleared each page and wrote its pre-image back, outside the tree's
/// latch and with nothing a row read could check. A seek that read the tree meanwhile saw cleared
/// leaves and failed with "Page 0 is the file header" (an entry reference of zero), reported the
/// root as index corruption, or silently returned no row for a committed key; a seek that read
/// the heap page meanwhile found its committed row's page cleared and skipped the row as
/// reclaimed. The rollback now restores the tree's pages under the tree latch, and every page in
/// one copy that a row read confirms against.
/// </summary>
public sealed class SqlUniqueViolationRollbackRaceTests
{
    private const int SeededRows = 200;
    private const int RecentKeyBase = 2_000_000;

    /// <summary>
    /// Two writers insert 40 fresh keys and then a committed one, which fails each statement after
    /// its 40 index inserts (and the leaf splits they cause); four readers seek committed keys
    /// meanwhile. Against the code before #1371 every one-second run of this workload failed, within
    /// about 45 ms on average, in all three ways above (20 of 20 runs, Release).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Unique violation: seeks racing failed multi-row inserts' bracket rollbacks read every committed key exactly once (#1371)")]
    public async Task Seek_RacingUniqueViolationRollbacks_ShouldReadEveryCommittedKeyOnce()
    {
        // Arrange: 200 committed keys under a unique index.
        await using var engine = CreateEngine();
        var database = await SeedAsync(engine);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var race = new Race(stop, batchRows: 40);

        // Act
        var tasks = new List<Task>();
        for (int writer = 0; writer < 2; writer++)
        {
            int seed = writer + 1;
            tasks.Add(Task.Run(() => race.WriteFailingAsync(database, seed)));
        }

        for (int reader = 0; reader < 4; reader++)
        {
            int seed = reader + 101;
            tasks.Add(Task.Run(() => race.ReadSeededAsync(database, seed)));
        }

        await Task.WhenAll(tasks);

        // Assert: no seek failed or missed its key, and the race ran on both sides.
        race.Failure.ShouldBeNull();
        race.Rollbacks.ShouldBeGreaterThan(0);
        race.Reads.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A committer inserts one fresh key per statement while two writers insert 10 fresh keys and
    /// then a committed one, so the committed rows and the failed statements' rows share the
    /// table's current heap page and every rollback restores it; four readers seek and range-scan
    /// the newest committed keys through the unique index. Against the code that restored only the
    /// index pages under the tree latch, a seek returned no row for a committed key, or a range
    /// missed one, on each of three runs (Release).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Unique violation: reads of committed rows on the heap page failed inserts keep rolling back find every row (#1371)")]
    public async Task Seek_CommittedRowsOnAHeapPageThatRollbacksRestore_ShouldFindEveryRow()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await SeedAsync(engine);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var race = new Race(stop, batchRows: 10);

        // Act
        var tasks = new List<Task> { Task.Run(() => race.CommitRecentAsync(database)) };
        for (int writer = 0; writer < 2; writer++)
        {
            int seed = writer + 1;
            tasks.Add(Task.Run(() => race.WriteFailingAsync(database, seed)));
        }

        for (int reader = 0; reader < 4; reader++)
        {
            int seed = reader + 101;
            tasks.Add(Task.Run(() => race.ReadRecentAsync(database, seed)));
        }

        await Task.WhenAll(tasks);

        // Assert
        race.Failure.ShouldBeNull();
        race.Rollbacks.ShouldBeGreaterThan(0);
        race.Commits.ShouldBeGreaterThan(0);
        race.Reads.ShouldBeGreaterThan(0);
    }

    private static SqlDatabaseEngine CreateEngine() => SqlDatabaseEngine.Create("unique-violation-rollback-race", new SqlDatabaseEngineOptions
    {
        CheckpointInterval = TimeSpan.FromHours(1),
        PageWriteBackInterval = TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    });

    /// <summary>Creates the table, its unique index and 200 committed keys.</summary>
    private static async Task<SqlDatabase> SeedAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("rollback-race-db");
        await using var setup = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)");
        await setup.ExecuteAsync("CREATE UNIQUE INDEX ux_val ON t (val)");
        for (int block = 0; block < SeededRows / 50; block++)
        {
            var values = Enumerable.Range(block * 50, 50).Select(value => $"({value}, {value})");
            await setup.ExecuteAsync($"INSERT INTO t (id, val) VALUES {string.Join(", ", values)}");
        }

        return database;
    }

    private static async Task<List<int>> ValuesAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token());
        var values = new List<int>();
        await foreach (var row in result.ShouldBeAssignableTo<QueryResultSet>()!.GetRowsAsync())
        {
            values.Add(Convert.ToInt32(row.GetValue(0)));
        }

        return values;
    }

    private sealed class Race(CancellationTokenSource stop, int batchRows)
    {
        private Exception? _failure;
        private long _rollbacks;
        private long _commits;
        private long _reads;
        private int _nextKey = 1_000_000;

        // The committer's keys are RecentKeyBase + n for n below this count, published once committed.
        private int _committed;

        public Exception? Failure => Volatile.Read(ref _failure);

        public long Rollbacks => Interlocked.Read(ref _rollbacks);

        public long Commits => Interlocked.Read(ref _commits);

        public long Reads => Interlocked.Read(ref _reads);

        private bool Running => !stop.IsCancellationRequested && Failure is null;

        /// <summary>
        /// Inserts batches of fresh keys that end in a seeded key, so each statement fails on the
        /// unique index after it has inserted the batch's rows and index entries.
        /// </summary>
        public async Task WriteFailingAsync(SqlDatabase database, int seed)
        {
            await using var session = await database.CreateSessionAsync();
            var random = new Random(seed);
            while (Running)
            {
                int first = Interlocked.Add(ref _nextKey, batchRows) - batchRows;
                int duplicate = random.Next(SeededRows);
                var rows = Enumerable.Range(first, batchRows).Select(value => $"({value}, {value})").Append($"({duplicate}, {duplicate})");
                try
                {
                    await session.ExecuteAsync($"INSERT INTO t (id, val) VALUES {string.Join(", ", rows)}", cancellationToken: TestTimeout.Token());
                    Fail(new InvalidOperationException($"The batch ending in committed key {duplicate} was inserted."));
                }
                catch (SqlConstraintViolationException)
                {
                    Interlocked.Increment(ref _rollbacks);
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }
        }

        /// <summary>Commits one fresh key per statement and publishes it once committed.</summary>
        public async Task CommitRecentAsync(SqlDatabase database)
        {
            await using var session = await database.CreateSessionAsync();
            while (Running)
            {
                int key = RecentKeyBase + Volatile.Read(ref _committed);
                try
                {
                    await session.ExecuteAsync($"INSERT INTO t (id, val) VALUES ({key}, {key})", cancellationToken: TestTimeout.Token());
                    Interlocked.Increment(ref _committed);
                    Interlocked.Increment(ref _commits);
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }
        }

        /// <summary>Seeks seeded keys and checks each finds exactly its row.</summary>
        public async Task ReadSeededAsync(SqlDatabase database, int seed)
        {
            await using var session = await database.CreateSessionAsync();
            var random = new Random(seed);
            while (Running)
            {
                int key = random.Next(SeededRows);
                try
                {
                    var values = await ValuesAsync(session, $"SELECT id FROM t WHERE val = {key}");
                    if (values.Count != 1 || values[0] != key || session.LastStatementMetrics?.AccessPath != "seek:ux_val")
                    {
                        Fail(new InvalidOperationException(
                            $"The seek of committed key {key} returned [{string.Join(", ", values)}] through '{session.LastStatementMetrics?.AccessPath}'."));
                    }

                    Interlocked.Increment(ref _reads);
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }
        }

        /// <summary>
        /// Seeks one of the 64 newest committed keys, or range-scans a run of them, and checks
        /// every committed key in the range is found once.
        /// </summary>
        public async Task ReadRecentAsync(SqlDatabase database, int seed)
        {
            await using var session = await database.CreateSessionAsync();
            var random = new Random(seed);
            while (Running)
            {
                int committed = Volatile.Read(ref _committed);
                if (committed == 0)
                {
                    await Task.Yield();
                    continue;
                }

                int low = RecentKeyBase + Math.Max(0, committed - 64) + random.Next(Math.Min(committed, 64));
                int high = random.Next(2) == 0 ? low : Math.Min(low + random.Next(1, 16), RecentKeyBase + committed - 1);
                try
                {
                    var values = await ValuesAsync(session, $"SELECT val FROM t WHERE val >= {low} AND val <= {high}");
                    values.Sort();
                    var expected = Enumerable.Range(low, high - low + 1).ToList();

                    // Keys the committer committed after this read began may be in the result too.
                    var missing = expected.Except(values).ToList();
                    if (missing.Count > 0 || values.Count != values.Distinct().Count() || values.Any(value => value < low || value > high))
                    {
                        Fail(new InvalidOperationException(
                            $"The read of committed keys {low}..{high} returned [{string.Join(", ", values)}], missing [{string.Join(", ", missing)}], through '{session.LastStatementMetrics?.AccessPath}'."));
                    }

                    Interlocked.Increment(ref _reads);
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }
        }

        private void Fail(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);
    }
}
