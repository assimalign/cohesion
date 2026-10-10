using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

/// <summary>
/// The graph client's diagnostics: each query's start, stop and failure.
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
/// query ends when its enumeration does: <c>Success</c> once it reaches the server's terminal count,
/// <c>Cancelled</c> when its caller disposes it early or cancels it, <c>Error</c> when a read fails;
/// <c>rowCount</c> is the paths read by then. A query writes its database, operation, row count and
/// how it ended; never its statement text or parameter values (plan D8). A failure is written by its
/// wire code and exception type only, never a message: the server's message for a parse error quotes
/// the statement (the area's failure rule, <c>docs/resources/Database/DESIGN.md</c>). Every
/// <c>QueryStart</c> is closed by one <c>QueryStop</c>, after <c>QueryFailed</c> for a failure. No
/// counters (plan D6).
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Graph.Client")]
internal sealed class GraphClientEventSource : EventSource
{
    /// <summary>The status of a query that completed.</summary>
    internal const string StatusSuccess = "Success";

    /// <summary>The status of a query that failed.</summary>
    internal const string StatusError = "Error";

    /// <summary>The status of a query its caller cancelled, or a path query its caller stopped reading.</summary>
    internal const string StatusCancelled = "Cancelled";

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
    /// Records what a query threw and returns false, so the exception filter that calls it catches
    /// nothing; the query writes its end from the <c>finally</c> of the same <c>try</c>.
    /// </summary>
    /// <param name="exception">What the query threw.</param>
    /// <param name="captured">Receives <paramref name="exception"/>.</param>
    /// <returns>False, always.</returns>
    public static bool CaptureFailure(Exception exception, out Exception captured)
    {
        captured = exception;
        return false;
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
    /// <returns>Whether the start was written: only then does the query's end write its <c>QueryStop</c>.</returns>
    [NonEvent]
    public bool QueryStart(GraphConnection connection, string operation)
    {
        if (!IsEnabled(EventLevel.Verbose, Keywords.Queries))
        {
            return false;
        }

        QueryStart(connection.Database, operation);
        return true;
    }

    /// <summary>
    /// Writes the end of a query on every path: <c>QueryFailed</c> first for a failure, then
    /// <c>QueryStop</c> with the query's status when its start was written, as
    /// <c>System.Net.Http</c>'s <c>RequestStop</c>. <c>QueryFailed</c> is written either way.
    /// </summary>
    /// <param name="connection">The connection that ran the query.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="startWritten">What <see cref="QueryStart(GraphConnection, string)"/> returned.</param>
    /// <param name="status"><see cref="StatusSuccess"/>, <see cref="StatusError"/> or <see cref="StatusCancelled"/>.</param>
    /// <param name="failure">The failure, for <see cref="StatusError"/>; otherwise <see langword="null"/>.</param>
    /// <param name="rowCount">The rows, or for a path query the paths, it returned or read.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the query started.</param>
    [NonEvent]
    public void QueryEnded(GraphConnection connection, string operation, bool startWritten, string status, Exception? failure, long rowCount, long startTimestamp)
    {
        if (failure is not null && IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            QueryFailed(connection.Database, operation, GetCode(failure), TypeName(failure), GetElapsedMilliseconds(startTimestamp));
        }

        if (startWritten && IsEnabled(EventLevel.Verbose, Keywords.Queries))
        {
            QueryStop(connection.Database, operation, status, rowCount, GetElapsedMilliseconds(startTimestamp));
        }
    }

    /// <summary>
    /// Writes the end of a query whose call threw or returned: the status follows from
    /// <paramref name="failure"/>, a cancellation being no failure.
    /// </summary>
    /// <param name="connection">The connection that ran the query.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="startWritten">What <see cref="QueryStart(GraphConnection, string)"/> returned.</param>
    /// <param name="failure">What the query threw, or <see langword="null"/> when it completed.</param>
    /// <param name="rowCount">The rows it returned; -1 when it threw.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the query started.</param>
    [NonEvent]
    public void QueryEnded(GraphConnection connection, string operation, bool startWritten, Exception? failure, long rowCount, long startTimestamp)
    {
        if (failure is null)
        {
            QueryEnded(connection, operation, startWritten, StatusSuccess, null, rowCount, startTimestamp);
        }
        else if (failure is OperationCanceledException)
        {
            QueryEnded(connection, operation, startWritten, StatusCancelled, null, rowCount, startTimestamp);
        }
        else
        {
            QueryEnded(connection, operation, startWritten, StatusError, failure, rowCount, startTimestamp);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Queries, Message = "Graph {1} on '{0}' started.")]
    private void QueryStart(string database, string operation)
        => WriteEvent(1, database, operation);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Queries, Message = "Graph {1} on '{0}' ended {2}: {3} row(s) in {4} ms.")]
    private void QueryStop(string database, string operation, string status, long rowCount, double durationMilliseconds)
        => WriteEvent(2, database, operation, status, rowCount, durationMilliseconds);

    [Event(3, Level = EventLevel.Error, Message = "Graph {1} on '{0}' failed after {4} ms: code '{2}', exception '{3}'.")]
    private void QueryFailed(string database, string operation, string code, string exceptionType, double durationMilliseconds)
        => WriteEvent(3, database, operation, code, exceptionType, durationMilliseconds);

    private static string GetCode(Exception failure) => failure switch
    {
        GraphClientException graph => graph.Code.ToString(),
        DatabaseClientException client => client.Code.ToString(),
        _ => string.Empty,
    };

    private static string TypeName(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;

    private static double GetElapsedMilliseconds(long startTimestamp)
        => startTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
