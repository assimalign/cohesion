using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents the result of a query execution.
/// </summary>
public abstract class QueryResult
{
    /// <summary>
    /// Gets the outcome status of the query execution.
    /// </summary>
    public abstract QueryResultStatus Status { get; }

    /// <summary>
    /// Gets the number of records affected by the operation.
    /// </summary>
    /// <remarks>
    /// For DML statements (INSERT, UPDATE, DELETE) this reflects the number of modified records.
    /// For queries that return rows, this value may be -1 when the count is not known upfront.
    /// </remarks>
    public abstract long AffectedCount { get; }

    /// <summary>
    /// Gets any diagnostic messages produced during parsing or execution.
    /// </summary>
    /// <remarks>
    /// Uses the <see cref="Diagnostic"/> type from <c>Database.Language</c> which carries
    /// source location information (start, end, line) in addition to code, message, and severity.
    /// </remarks>
    public abstract IReadOnlyList<Diagnostic>? Diagnostics { get; }
}
