using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

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
