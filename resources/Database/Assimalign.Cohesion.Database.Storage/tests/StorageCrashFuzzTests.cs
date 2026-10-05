using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// A seeded mixed workload — inserts, in-place updates, deletes that free pages, rollbacks,
/// steals through a three-page pool, and checkpoints — loses power at every write it makes to the
/// data file or the journal, whole and torn (#1253). After each crash the reopened storage must
/// hold exactly the acknowledged commits: the reference model, or, for a crash inside a commit,
/// the model with that commit or without it.
/// </summary>
/// <remarks>
/// Each seed's workload makes several hundred writes, every one a crash point. Set
/// <c>COHESION_STORAGE_CRASH_FUZZ_SEEDS</c> to run more seeds (each adds its own write points;
/// 40 seeds pass ten thousand).
/// </remarks>
public sealed class StorageCrashFuzzTests
{
    private const int Owners = 3;
    private readonly ITestOutputHelper _output;

    public StorageCrashFuzzTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Storage] - Crash fuzz: a crash at every write of a mixed workload recovers exactly the acknowledged commits")]
    public void Workload_CrashAtEveryWrite_ShouldRecoverTheAcknowledgedCommits()
    {
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("COHESION_STORAGE_CRASH_FUZZ_SEEDS"), out int configured) && configured > 0
            ? configured
            : 2;
        int points = 0;

        for (int seed = 1; seed <= seeds; seed++)
        {
            // The dry run counts the workload's writes and checks the uncrashed outcome.
            var dryPoint = new CrashPoint();
            var dry = Run(seed, dryPoint, crashAtWrite: 0);
            dry.Crashed.ShouldBeFalse();
            int writes = dryPoint.Writes - dry.CreationWrites;
            writes.ShouldBeGreaterThan(100);

            for (int write = 1; write <= writes; write++)
            {
                var point = new CrashPoint { DurableSectors = (write * 7) % 17 };
                var run = Run(seed, point, write);
                run.Crashed.ShouldBeTrue($"seed {seed}, write {write}");
                string at = $"seed {seed}, write {write} ({point.Log[point.CrashAtWrite - 1]}), {point.DurableSectors} sectors";

                using var recovered = TornStorage.Open(run.Storage.CaptureDurable());
                var actual = Snapshot(recovered);
                bool matches = Matches(actual, run.Committed) || (run.InFlight is not null && Matches(actual, run.InFlight));
                matches.ShouldBeTrue($"{at}: recovered state is neither the acknowledged commits nor those plus the one in flight");
                points++;
            }
        }

        _output.WriteLine($"{seeds} seeds, {points} crash points");
    }

    /// <summary>
    /// Runs the seeded workload until it ends or loses power, and returns the acknowledged state,
    /// and the state with the commit in flight when power was lost during a commit.
    /// </summary>
    private static Outcome Run(int seed, CrashPoint point, int crashAtWrite)
    {
        var random = new Random(seed);
        var storage = TornStorage.Create(point, poolCapacity: 3);
        int creationWrites = point.Writes;
        if (crashAtWrite > 0)
        {
            point.CrashAtWrite = creationWrites + crashAtWrite;
        }
        var committed = new Dictionary<(PageId, int), (ulong Owner, byte[] Record)>();
        var working = new Dictionary<(PageId, int), (ulong Owner, byte[] Record)>();
        bool committing = false;

        try
        {
            for (int step = 0; step < 60; step++)
            {
                working = new Dictionary<(PageId, int), (ulong Owner, byte[] Record)>(committed);
                using var transaction = storage.BeginTransaction();
                int operations = random.Next(1, 5);
                for (int i = 0; i < operations; i++)
                {
                    int kind = random.Next(10);
                    if (kind < 4 || working.Count < 4)
                    {
                        ulong owner = (ulong)random.Next(1, Owners + 1);
                        var record = Record(random, random.Next(50, 2500));
                        var location = storage.Insert(transaction, owner, record);
                        working[location] = (owner, record);
                    }
                    else if (kind < 8)
                    {
                        var key = working.Keys.ElementAt(random.Next(working.Count));
                        var (owner, old) = working[key];
                        var record = Record(random, old.Length);
                        storage.Update(transaction, key.Item1, key.Item2, record);
                        working[key] = (owner, record);
                    }
                    else
                    {
                        var key = working.Keys.ElementAt(random.Next(working.Count));
                        storage.Delete(transaction, key.Item1, key.Item2);
                        working.Remove(key);
                    }
                }

                if (random.Next(6) == 0)
                {
                    storage.PageManager.FlushAll();
                }

                if (random.Next(4) == 0)
                {
                    transaction.Rollback();
                }
                else
                {
                    committing = true;
                    transaction.Commit();
                    committing = false;
                    committed = working;
                }

                if (random.Next(8) == 0)
                {
                    storage.Checkpoint();
                }
            }
        }
        catch (Exception exception) when (point.HasCrashed && IsPowerLoss(exception))
        {
            return new Outcome(storage, true, committed, committing ? working : null, creationWrites);
        }

        return new Outcome(storage, false, committed, null, creationWrites);
    }

    private static bool IsPowerLoss(Exception exception)
        => exception is SimulatedPowerLossException
        || exception.InnerException is SimulatedPowerLossException
        || exception is StorageOfflineException;

    private static byte[] Record(Random random, int length)
    {
        var record = new byte[length];
        random.NextBytes(record);
        return record;
    }

    private static Dictionary<ulong, List<string>> Snapshot(TornStorage storage)
    {
        var snapshot = new Dictionary<ulong, List<string>>();
        for (ulong owner = 1; owner <= Owners; owner++)
        {
            snapshot[owner] = [.. storage.ScanOwner(owner).Select(Convert.ToHexString).Order(StringComparer.Ordinal)];
        }

        return snapshot;
    }

    private static bool Matches(Dictionary<ulong, List<string>> actual, Dictionary<(PageId, int), (ulong Owner, byte[] Record)> model)
    {
        for (ulong owner = 1; owner <= Owners; owner++)
        {
            var expected = model.Values.Where(value => value.Owner == owner).Select(value => Convert.ToHexString(value.Record)).Order(StringComparer.Ordinal);
            if (!actual[owner].SequenceEqual(expected))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Outcome(
        TornStorage Storage,
        bool Crashed,
        Dictionary<(PageId, int), (ulong Owner, byte[] Record)> Committed,
        Dictionary<(PageId, int), (ulong Owner, byte[] Record)>? InFlight,
        int CreationWrites);
}
