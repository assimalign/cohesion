using System.Diagnostics;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

/// <summary>
/// The graph client's diagnostics: each query's start, stop and coded failure.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Graph.Client</c>; applications forward
/// it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The connection itself is
/// reported by the shared client core's source, <c>Assimalign.Cohesion.Database.Client</c>.
/// </para>
/// <para>
/// <c>operation</c> names the public member: <c>Query</c>, <c>Execute</c> or <c>QueryPaths</c>. A path
/// query stops when its enumeration reaches the server's terminal count, and <c>rowCount</c> is then
/// the number of paths. A query writes its database, operation, row count and wire code; never its
/// statement text or parameter values (plan D8). No counters (plan D6).
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Graph.Client")]
internal sealed class GraphClientEventSource : EventSource
{
    public static readonly GraphClientEventSource Log = new();

    private GraphClientEventSource()
    {
    }

    /// <summary>
    /// The keywords that let a tool take one family of Verbose events without the others.
    /// </summary>
    public static class Keywords
    {
        /// <summary>The per-query trace: <c>QueryStart</c> and <c>QueryStop</c>.</summary>
        public const EventKeywords Queries = (EventKeywords)0x1;
    }

    /// <summary>
    /// Takes a timestamp for a query's duration, only while a listener takes the source.
    /// </summary>
    /// <returns>The timestamp, or zero when nobody listens.</returns>
    [NonEvent]
    public long GetTimestamp()
        => IsEnabled() ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Writes the start of a query.
    /// </summary>
    /// <param name="connection">The connection that runs the query.</param>
    /// <param name="operation">The public member that runs it.</param>
    [NonEvent]
    public void QueryStart(GraphConnection connection, string operation)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Queries))
        {
            QueryStart(connection.Database, operation);
        }
    }

    /// <summary>
    /// Writes the successful end of a query.
    /// </summary>
    /// <param name="connection">The connection that ran the query.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="rowCount">The rows, or for a path query the paths, it returned.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the query started.</param>
    [NonEvent]
    public void QueryStop(GraphConnection connection, string operation, long rowCount, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Queries))
        {
            QueryStop(connection.Database, operation, rowCount, GetElapsedMilliseconds(startTimestamp));
        }
    }

    /// <summary>
    /// Writes a query that failed with a coded error.
    /// </summary>
    /// <param name="connection">The connection that ran the query.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="exception">The shared client's coded failure.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the query started.</param>
    [NonEvent]
    public void QueryFailed(GraphConnection connection, string operation, DatabaseClientException exception, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            QueryFailed(connection.Database, operation, exception.Code.ToString(), exception.Message, GetElapsedMilliseconds(startTimestamp));
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Queries, Message = "Graph {1} on '{0}' started.")]
    private void QueryStart(string database, string operation)
        => WriteEvent(1, database, operation);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Queries, Message = "Graph {1} on '{0}' completed: {2} row(s) in {3} ms.")]
    private void QueryStop(string database, string operation, long rowCount, double durationMilliseconds)
        => WriteEvent(2, database, operation, rowCount, durationMilliseconds);

    [Event(3, Level = EventLevel.Error, Message = "Graph {1} on '{0}' failed ({2}): {3}. After {4} ms.")]
    private void QueryFailed(string database, string operation, string code, string exceptionMessage, double durationMilliseconds)
        => WriteEvent(3, database, operation, code, exceptionMessage, durationMilliseconds);

    private static double GetElapsedMilliseconds(long startTimestamp)
        => startTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
