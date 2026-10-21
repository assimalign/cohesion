using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>What one probe run saw.</summary>
/// <param name="Ran">How long the workers ran before the run stopped starting statements.</param>
/// <param name="Statements">The statements that completed.</param>
/// <param name="Commits">The transactions that committed, auto-commit statements included.</param>
/// <param name="Deadlocks">The statements chosen as deadlock victims.</param>
/// <param name="Conflicts">The statements and commits that failed a write conflict.</param>
/// <param name="Violations">The statements a constraint refused.</param>
/// <param name="Waits">The lock requests that waited.</param>
/// <param name="ParentDeletes">The cascading parent deletes that committed: whether the deleters made progress.</param>
/// <param name="SlowWaits">The waits that lasted at least <see cref="SqlLockWaitProbe.SlowWait"/>.</param>
/// <param name="Unexpected">Every other failure, by type and message.</param>
internal sealed record SqlLockWaitProbeResult(
    TimeSpan Ran,
    long Statements,
    long Commits,
    long Deadlocks,
    long Conflicts,
    long Violations,
    long Waits,
    long ParentDeletes,
    IReadOnlyList<SqlLockWait> SlowWaits,
    IReadOnlyDictionary<string, int> Unexpected)
{
    /// <summary>The recorded waits that lasted at least <paramref name="bound"/>.</summary>
    public IReadOnlyList<SqlLockWait> WaitsOf(TimeSpan bound) => SlowWaits.Where(wait => wait.Milliseconds >= bound.TotalMilliseconds).ToArray();

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"ran {Ran.TotalSeconds:F1} s: {Statements} statements, {Commits} commits, {Deadlocks} deadlock victims, ")
            .Append(CultureInfo.InvariantCulture, $"{Conflicts} conflicts, {Violations} constraint violations, {Waits} lock waits, ")
            .Append(CultureInfo.InvariantCulture, $"{ParentDeletes} parent deletes committed; ")
            .Append(CultureInfo.InvariantCulture, $"waits of {SqlLockWaitProbe.SlowWait.TotalSeconds} s or more: {SlowWaits.Count}, ")
            .Append(CultureInfo.InvariantCulture, $"of {SqlLockWaitProbe.LongWait.TotalSeconds} s or more: {WaitsOf(SqlLockWaitProbe.LongWait).Count}");
        foreach (var wait in SlowWaits)
        {
            text.AppendLine().Append("  ").Append(wait);
        }

        foreach (var (error, count) in Unexpected)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture, $"  unexpected x{count}: {error}");
        }

        return text.ToString();
    }
}
