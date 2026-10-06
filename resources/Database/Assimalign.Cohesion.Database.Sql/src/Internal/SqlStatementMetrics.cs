namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Per-statement execution observability: how the statement reached its rows and
/// how many stored records it had to examine. This is the behavioral proof
/// surface for access-path work — a seek must demonstrably examine O(matches)
/// records where the equivalent scan examines O(table) — consumed by tests
/// through the session's last-statement view.
/// </summary>
/// <remarks>
/// It also records the durable brackets a self-committing statement (DDL) committed by itself,
/// which decide how the session reports the statement's failure on an offline storage (#1272).
/// </remarks>
internal sealed class SqlStatementMetrics
{
    /// <summary>
    /// Gets or sets the number of stored records the statement examined: units
    /// decoded by a scan, or entries fetched by an index seek.
    /// </summary>
    internal long RecordsExamined { get; set; }

    /// <summary>
    /// Gets or sets the access path the executor drove ("scan", or
    /// "seek:&lt;index&gt;", "join-scan", or "join-seek:&lt;index&gt;"), empty for
    /// statements with no table access.
    /// </summary>
    internal string AccessPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets the number of durable brackets the statement committed by itself: a DDL statement's
    /// catalog self-commits and its durably committed data brackets, each counted once its commit
    /// returned. Zero for every other statement, and for a DDL statement that met an offline
    /// storage before it committed anything, which is then reported as refused rather than
    /// unconfirmed (#1272).
    /// </summary>
    internal int SelfCommits { get; private set; }

    /// <summary>
    /// Records one durable bracket the statement committed by itself (<see cref="SelfCommits"/>).
    /// </summary>
    internal void RecordSelfCommit() => SelfCommits++;
}
