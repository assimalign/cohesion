using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents the result of executing a request: its status, the records it changed, and the
/// diagnostics the model reported.
/// </summary>
public abstract class DatabaseResult
{
    /// <summary>
    /// Gets the outcome status of the execution.
    /// </summary>
    public abstract DatabaseResultStatus Status { get; }

    /// <summary>
    /// Gets the number of records the request changed, or -1 when the model reports no count.
    /// </summary>
    /// <remarks>
    /// A request that writes or deletes records reports how many it changed. A request that
    /// returns rows may report -1 when the count is not known up front.
    /// </remarks>
    public abstract long AffectedCount { get; }

    /// <summary>
    /// Gets any diagnostics the model reported while it parsed or executed the request.
    /// </summary>
    /// <remarks>
    /// Uses the <see cref="Diagnostic"/> type from <c>Database.Types</c>: a code, a message and a
    /// severity, and, for a diagnostic a language model found in statement text, its position in
    /// that text (start, end, line). A model that executes typed requests sets no position.
    /// </remarks>
    public abstract IReadOnlyList<Diagnostic>? Diagnostics { get; }
}
