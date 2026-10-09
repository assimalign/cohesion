using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// The adversarial read-committed probe of #1363 for the key-value model: readers run one command
/// per explicit <see cref="IsolationLevel.ReadCommitted"/> transaction (a full range scan, a batch
/// of point reads in one transaction), writers replace and delete keys in snapshot transactions
/// they hold open for a moment, and a loop runs the version purge as fast as it can. Every committed
/// state holds every key exactly once, so a command that reads one consistent snapshot finds every
/// key: a key it does not find went missing under it.
/// </summary>
internal static class KeyValueReadCommittedProbe
{
    /// <summary>The keys every committed state holds.</summary>
    internal const int KeyCount = 400;

    /// <summary>The command kinds the readers run, in turn.</summary>
    internal static readonly string[] Kinds = ["scan", "get"];

    internal static byte[] Key(int index) => Encoding.ASCII.GetBytes($"k{index:D4}");

    /// <summary>Runs the probe for the given duration and returns what it counted.</summary>
    /// <param name="duration">How long the readers, writers and purge loop run.</param>
    /// <param name="readers">The number of reader sessions.</param>
    /// <param name="writers">The number of writer sessions.</param>
    /// <returns>The counts.</returns>
    internal static async Task<KeyValueReadCommittedProbeCounts> RunAsync(TimeSpan duration, int readers = 4, int writers = 2)
    {
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "rc-command-probe" });
        var database = await engine.CreateDatabaseAsync("probe");
        await using (var setup = await database.CreateSessionAsync())
        {
            var transaction = await setup.BeginTransactionAsync();
            for (int i = 0; i < KeyCount; i++)
            {
                await database.PutAsync(setup, Key(i), BitConverter.GetBytes(0L));
            }

            await transaction.CommitAsync();
        }

        var counts = new KeyValueReadCommittedProbeCounts();
        using var stop = new CancellationTokenSource(duration);
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

        actors.Add(Task.Run(() => PurgeAsync(database, counts, stop.Token)));
        await Task.WhenAll(actors);
        return counts;
    }

    private static async Task WriteAsync(KeyValueDatabase database, KeyValueReadCommittedProbeCounts counts, int seed, CancellationToken stop)
    {
        var random = new Random(seed);
        await using var session = await database.CreateSessionAsync();
        long version = 0;

        while (!stop.IsCancellationRequested)
        {
            var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot);
            try
            {
                int operations = random.Next(1, 6);
                for (int i = 0; i < operations; i++)
                {
                    byte[] key = Key(random.Next(KeyCount));
                    if (random.Next(2) == 0)
                    {
                        await database.PutAsync(session, key, BitConverter.GetBytes(++version));
                    }
                    else
                    {
                        await database.TryDeleteAsync(session, key);
                        await database.PutAsync(session, key, BitConverter.GetBytes(++version));
                    }
                }

                // Held open for a moment, so readers begin while it is in flight and their
                // commands are still reading when it commits.
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

    private static async Task ReadAsync(KeyValueDatabase database, KeyValueReadCommittedProbeCounts counts, int first, CancellationToken stop)
    {
        var random = new Random(first);
        await using var session = await database.CreateSessionAsync();
        int turn = first;

        while (!stop.IsCancellationRequested)
        {
            string kind = Kinds[turn++ % Kinds.Length];
            var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                if (kind == "scan")
                {
                    var keys = new List<string>(KeyCount);
                    await foreach (var entry in database.ScanAsync(session))
                    {
                        keys.Add(Encoding.ASCII.GetString(entry.Key.Span));
                    }

                    counts.CountScan(keys);
                }
                else
                {
                    for (int i = 0; i < 8; i++)
                    {
                        var entry = await database.GetAsync(session, Key(random.Next(KeyCount)));
                        counts.CountGet(entry is not null);
                    }
                }

                await transaction.CommitAsync();
            }
            catch (Exception exception)
            {
                counts.ReaderErrors.AddOrUpdate(kind + " " + exception.GetType().Name + ": " + exception.Message, 1, (_, value) => value + 1);
                await RollbackQuietlyAsync(transaction);
            }
        }
    }

    private static async Task PurgeAsync(KeyValueDatabase database, KeyValueReadCommittedProbeCounts counts, CancellationToken stop)
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

            await Task.Yield();
        }
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
            // The probe counts the command's failure; a failed rollback adds nothing to it.
        }
    }
}

/// <summary>What one run of <see cref="KeyValueReadCommittedProbe"/> counted.</summary>
internal sealed class KeyValueReadCommittedProbeCounts
{
    internal long WriterCommits;
    internal long WriterConflicts;
    internal long PurgePasses;
    internal long Reclaimed;
    internal long Scans;
    internal long AffectedScans;
    internal long ScanMissing;
    internal long ScanDuplicated;
    internal long Gets;
    internal long GetMissing;

    internal ConcurrentDictionary<string, long> ReaderErrors { get; } = new();
    internal ConcurrentDictionary<string, long> WriterErrors { get; } = new();
    internal ConcurrentDictionary<string, long> PurgeErrors { get; } = new();

    /// <summary>Gets the keys that went missing or were duplicated, and the reader errors.</summary>
    internal long Anomalies => ScanMissing + ScanDuplicated + GetMissing + ReaderErrors.Values.Sum();

    internal void CountScan(List<string> keys)
    {
        Interlocked.Increment(ref Scans);
        var distinct = new HashSet<string>(keys);
        long missing = KeyValueReadCommittedProbe.KeyCount - distinct.Count;
        long duplicated = keys.Count - distinct.Count;
        Interlocked.Add(ref ScanMissing, Math.Max(0, missing));
        Interlocked.Add(ref ScanDuplicated, duplicated);
        if (missing != 0 || duplicated != 0)
        {
            Interlocked.Increment(ref AffectedScans);
        }
    }

    internal void CountGet(bool found)
    {
        Interlocked.Increment(ref Gets);
        if (!found)
        {
            Interlocked.Increment(ref GetMissing);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder();
        text.AppendLine($"writer commits {WriterCommits}, writer conflicts {WriterConflicts}, purge passes {PurgePasses}, versions reclaimed {Reclaimed}");
        text.AppendLine($"scan: commands {Scans}, affected commands {AffectedScans}, missing keys {ScanMissing}, duplicated keys {ScanDuplicated}");
        text.AppendLine($"get: commands {Gets}, missing keys {GetMissing}");
        Append(text, "reader errors", ReaderErrors);
        Append(text, "writer errors", WriterErrors);
        Append(text, "purge errors", PurgeErrors);
        return text.ToString();
    }

    private static void Append(StringBuilder text, string heading, ConcurrentDictionary<string, long> errors)
    {
        text.AppendLine($"{heading}: {errors.Values.Sum()}");
        foreach (var (message, count) in errors.OrderByDescending(pair => pair.Value).Take(8))
        {
            text.AppendLine($"  {count} x {message}");
        }
    }
}
