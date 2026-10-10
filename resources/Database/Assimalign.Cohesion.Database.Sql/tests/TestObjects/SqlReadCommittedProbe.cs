using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>
/// The adversarial read-committed probe of #1363: readers run one long statement per explicit
/// <see cref="IsolationLevel.ReadCommitted"/> transaction (a scan, an index seek, an index join and
/// a nested-loop join), writers replace and delete rows in snapshot transactions they hold open
/// for a moment, and a loop runs the version purge as fast as it can. Every committed state holds
/// each id exactly once in both tables, so a statement that reads one consistent snapshot returns
/// every id once: a row it does not return went missing under it.
/// </summary>
/// <remarks>
/// The race it looks for needs a writer that began before the reader's transaction, is still in
/// flight when the statement captures its snapshot, and commits while the statement reads: a
/// read-committed transaction holds the prune bound only at its own sequence, so without a pin of
/// its own the statement's older floor is not covered once that writer commits.
/// </remarks>
internal static class SqlReadCommittedProbe
{
    /// <summary>The ids every committed state holds, once each, in both tables.</summary>
    internal const int RowCount = 120;

    /// <summary>The statement kinds the readers run, in turn.</summary>
    internal static readonly string[] Kinds = ["scan", "seek", "index-join", "nested-loop-join"];

    /// <summary>The longest a run waits past its duration for the work it needs.</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    /// <summary>The writer commits a run needs before it may stop.</summary>
    internal const int MinimumWriterCommits = 20;

    /// <summary>Runs the probe and returns what it counted.</summary>
    /// <param name="duration">
    /// The shortest time the readers, writers and purge loop run. The run then continues until the
    /// purge has reclaimed a version and the writers have committed <see cref="MinimumWriterCommits"/>
    /// times, at most <see cref="Ceiling"/> longer, so a slow machine cannot pass without the race
    /// having been exercised (macOS CI once ran 2 s with 6 commits and no purge pass).
    /// </param>
    /// <param name="readers">The number of reader sessions.</param>
    /// <param name="writers">The number of writer sessions.</param>
    /// <returns>The counts.</returns>
    internal static async Task<SqlReadCommittedProbeCounts> RunAsync(TimeSpan duration, int readers = 4, int writers = 2)
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "rc-statement-probe" });
        var database = await engine.CreateDatabaseAsync("probe");
        await using (var setup = await database.CreateSessionAsync())
        {
            await setup.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL, k2 INT NOT NULL)");
            await setup.ExecuteAsync("CREATE INDEX ix_t_id ON t (id)");
            await setup.ExecuteAsync("CREATE TABLE u (id INT NOT NULL, tag INT NOT NULL)");
            await setup.ExecuteAsync("CREATE INDEX ix_u_id ON u (id)");
            for (int start = 1; start <= RowCount; start += 40)
            {
                var ids = Enumerable.Range(start, Math.Min(40, RowCount - start + 1)).ToArray();
                await setup.ExecuteAsync("INSERT INTO t (id, val, k2) VALUES " + string.Join(", ", ids.Select(id => $"({id}, 0, {id})")));
                await setup.ExecuteAsync("INSERT INTO u (id, tag) VALUES " + string.Join(", ", ids.Select(id => $"({id}, {id})")));
            }
        }

        var counts = new SqlReadCommittedProbeCounts();
        using var stop = new CancellationTokenSource();
        var actors = new List<Task>();

        for (int i = 0; i < writers; i++)
        {
            int seed = 1000 + i;
            actors.Add(Task.Run(() => WriteAsync(database, counts, seed, stop.Token)));
        }

        for (int i = 0; i < readers; i++)
        {
            int first = i;
            actors.Add(Task.Run(() => ReadAsync(database, counts, first, stop.Token)));
        }

        // The purge loop has a thread of its own: on a small runner the readers and writers hold
        // the pool's few threads, and a pooled purge loop could wait out the whole run.
        var purge = new Thread(() => Purge(database, counts, stop.Token)) { IsBackground = true, Name = "rc-probe-purge" };
        purge.Start();

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < duration || (!HasExercisedTheRace(counts) && elapsed.Elapsed < duration + Ceiling))
        {
            await Task.Delay(50);
        }

        stop.Cancel();
        await Task.WhenAll(actors);
        purge.Join();
        return counts;
    }

    private static bool HasExercisedTheRace(SqlReadCommittedProbeCounts counts)
        => Interlocked.Read(ref counts.Reclaimed) > 0 && Interlocked.Read(ref counts.WriterCommits) >= MinimumWriterCommits;

    private static async Task WriteAsync(SqlDatabase database, SqlReadCommittedProbeCounts counts, int seed, CancellationToken stop)
    {
        var random = new Random(seed);
        await using var session = await database.CreateSessionAsync();

        while (!stop.IsCancellationRequested)
        {
            var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot);
            try
            {
                int operations = random.Next(1, 4);
                for (int i = 0; i < operations; i++)
                {
                    int id = random.Next(1, RowCount + 1);
                    switch (random.Next(4))
                    {
                        case 0:
                            await session.ExecuteAsync($"UPDATE t SET val = val + 1 WHERE id = {id}");
                            break;
                        case 1:
                            await session.ExecuteAsync($"DELETE FROM t WHERE id = {id}");
                            await session.ExecuteAsync($"INSERT INTO t (id, val, k2) VALUES ({id}, 0, {id})");
                            break;
                        case 2:
                            await session.ExecuteAsync($"UPDATE u SET tag = tag WHERE id = {id}");
                            break;
                        default:
                            await session.ExecuteAsync($"DELETE FROM u WHERE id = {id}");
                            await session.ExecuteAsync($"INSERT INTO u (id, tag) VALUES ({id}, {id})");
                            break;
                    }
                }

                // Held open for a moment, so readers begin while it is in flight and their
                // statements are still reading when it commits.
                await Task.Delay(random.Next(0, 3));
                await transaction.CommitAsync();
                Interlocked.Increment(ref counts.WriterCommits);
            }
            catch (Exception exception) when (exception is DatabaseTransactionAbortedException or DatabaseTransactionDeadlockException)
            {
                Interlocked.Increment(ref counts.WriterConflicts);
                await RollbackQuietlyAsync(transaction);
            }
            catch (Exception exception)
            {
                counts.WriterErrors.AddOrUpdate(exception.GetType().Name + ": " + exception.Message, 1, (_, value) => value + 1);
                await RollbackQuietlyAsync(transaction);
            }
        }
    }

    private static async Task ReadAsync(SqlDatabase database, SqlReadCommittedProbeCounts counts, int first, CancellationToken stop)
    {
        var random = new Random(first);
        await using var session = await database.CreateSessionAsync();
        int turn = first;

        while (!stop.IsCancellationRequested)
        {
            string kind = Kinds[turn++ % Kinds.Length];
            int seek = random.Next(1, RowCount + 1);
            string sql = kind switch
            {
                "scan" => "SELECT id FROM t",
                "seek" => $"SELECT id FROM t WHERE id = {seek}",
                "index-join" => "SELECT t.id FROM t JOIN u ON t.id = u.id",
                _ => "SELECT t.id FROM t JOIN u ON t.k2 = u.tag",
            };

            var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var ids = await IdsAsync(session, sql);
                counts.Count(kind, ids, kind == "seek" ? [seek] : null);
                await transaction.CommitAsync();
            }
            catch (Exception exception)
            {
                counts.ReaderErrors.AddOrUpdate(kind + " " + exception.GetType().Name + ": " + exception.Message, 1, (_, value) => value + 1);
                await RollbackQuietlyAsync(transaction);
            }
        }
    }

    private static void Purge(SqlDatabase database, SqlReadCommittedProbeCounts counts, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Interlocked.Add(ref counts.Reclaimed, database.Coordinator.RunVersionPurgePass(CancellationToken.None));
                Interlocked.Increment(ref counts.PurgePasses);
            }
            catch (Exception exception)
            {
                counts.PurgeErrors.AddOrUpdate(exception.GetType().Name + ": " + exception.Message, 1, (_, value) => value + 1);
            }

            Thread.Yield();
        }
    }

    private static async Task<List<long>> IdsAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql);
        await using var set = (QueryResultSet)result;
        var ids = new List<long>();
        await foreach (var row in set.GetRowsAsync())
        {
            ids.Add(Convert.ToInt64(row.GetValue(0)));
        }

        return ids;
    }

    private static async Task RollbackQuietlyAsync(DatabaseTransaction transaction)
    {
        try
        {
            if (transaction.State == TransactionState.Active)
            {
                await transaction.RollbackAsync();
            }
        }
        catch (Exception)
        {
            // The probe counts the statement's failure; a failed rollback adds nothing to it.
        }
    }
}

/// <summary>What one run of <see cref="SqlReadCommittedProbe"/> counted.</summary>
internal sealed class SqlReadCommittedProbeCounts
{
    internal long WriterCommits;
    internal long WriterConflicts;
    internal long PurgePasses;
    internal long Reclaimed;

    internal ConcurrentDictionary<string, long> Statements { get; } = new();
    internal ConcurrentDictionary<string, long> Missing { get; } = new();
    internal ConcurrentDictionary<string, long> Duplicated { get; } = new();
    internal ConcurrentDictionary<string, long> Unexpected { get; } = new();
    internal ConcurrentDictionary<string, long> AffectedStatements { get; } = new();
    internal ConcurrentDictionary<string, long> ReaderErrors { get; } = new();
    internal ConcurrentDictionary<string, long> WriterErrors { get; } = new();
    internal ConcurrentDictionary<string, long> PurgeErrors { get; } = new();

    /// <summary>Gets the rows that went missing, were duplicated or were unexpected, and the reader errors.</summary>
    internal long Anomalies => Missing.Values.Sum() + Duplicated.Values.Sum() + Unexpected.Values.Sum() + ReaderErrors.Values.Sum();

    internal void Count(string kind, List<long> ids, long[]? expected)
    {
        Statements.AddOrUpdate(kind, 1, (_, value) => value + 1);
        var wanted = expected ?? Enumerable.Range(1, SqlReadCommittedProbe.RowCount).Select(id => (long)id).ToArray();
        var distinct = new HashSet<long>(ids);
        long missing = wanted.Count(id => !distinct.Contains(id));
        long duplicated = ids.Count - distinct.Count;
        long unexpected = distinct.Count(id => Array.IndexOf(wanted, id) < 0);

        if (missing > 0) { Missing.AddOrUpdate(kind, missing, (_, value) => value + missing); }
        if (duplicated > 0) { Duplicated.AddOrUpdate(kind, duplicated, (_, value) => value + duplicated); }
        if (unexpected > 0) { Unexpected.AddOrUpdate(kind, unexpected, (_, value) => value + unexpected); }
        if (missing + duplicated + unexpected > 0) { AffectedStatements.AddOrUpdate(kind, 1, (_, value) => value + 1); }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder();
        text.AppendLine($"writer commits {WriterCommits}, writer conflicts {WriterConflicts}, purge passes {PurgePasses}, versions reclaimed {Reclaimed}");
        foreach (string kind in SqlReadCommittedProbe.Kinds)
        {
            text.AppendLine($"{kind}: statements {Get(Statements, kind)}, affected statements {Get(AffectedStatements, kind)}, " +
                $"missing rows {Get(Missing, kind)}, duplicated rows {Get(Duplicated, kind)}, unexpected rows {Get(Unexpected, kind)}");
        }

        Append(text, "reader errors", ReaderErrors);
        Append(text, "writer errors", WriterErrors);
        Append(text, "purge errors", PurgeErrors);
        return text.ToString();
    }

    private static long Get(ConcurrentDictionary<string, long> values, string key) => values.TryGetValue(key, out long value) ? value : 0;

    private static void Append(StringBuilder text, string heading, ConcurrentDictionary<string, long> errors)
    {
        text.AppendLine($"{heading}: {errors.Values.Sum()}");
        foreach (var (message, count) in errors.OrderByDescending(pair => pair.Value).Take(8))
        {
            text.AppendLine($"  {count} x {message}");
        }
    }
}
