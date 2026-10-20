using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

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
