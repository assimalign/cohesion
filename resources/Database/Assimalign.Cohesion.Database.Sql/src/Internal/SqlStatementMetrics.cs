namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Per-statement execution observability: how the statement reached its rows and
/// how many stored records it had to examine. This is the behavioral proof
/// surface for access-path work — a seek must demonstrably examine O(matches)
/// records where the equivalent scan examines O(table) — consumed by tests
/// through the session's last-statement view.
/// </summary>
internal sealed class SqlStatementMetrics
{
    /// <summary>
    /// Gets or sets the number of stored records the statement examined: units
    /// decoded by a scan, or entries fetched by an index seek.
    /// </summary>
    internal long RecordsExamined { get; set; }

    /// <summary>
    /// Gets or sets the access path the executor drove ("scan", or
    /// "seek:&lt;index&gt;"), empty for statements with no table access.
    /// </summary>
    internal string AccessPath { get; set; } = string.Empty;
}
