using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.Sql.Client.Internal;

/// <summary>
/// The typed SQL client's diagnostics: each command's start, stop and failure, and the failures of
/// the application's <see cref="SqlClientObserver"/> hooks, which the client swallows.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Sql.Client</c>; applications forward
/// it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The connection itself is
/// reported by the shared client core's source, <c>Assimalign.Cohesion.Database.Client</c>.
/// </para>
/// <para>
/// A command writes its database, parameter count, row and affected counts, and how it ended; never
/// its statement text or parameter values (plan D8). A failure is written by its error kind, wire
/// code and exception type only, never a message: the server's message for a parse error quotes the
/// statement (the area's failure rule, <c>docs/resources/Database/DESIGN.md</c>). Every
/// <c>CommandStart</c> is closed by one <c>CommandStop</c>, whose <c>status</c> is <c>Success</c>,
/// <c>Error</c> (after <c>CommandFailed</c>) or <c>Cancelled</c>. No counters: a process-wide count
/// updated per command would be a contention point (plan D6).
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Sql.Client")]
internal sealed class SqlClientEventSource : EventSource
{
    /// <summary>The status of a command that returned its result.</summary>
    internal const string StatusSuccess = "Success";

    /// <summary>The status of a command that failed.</summary>
    internal const string StatusError = "Error";

    /// <summary>The status of a command its caller cancelled.</summary>
    internal const string StatusCancelled = "Cancelled";

    public static readonly SqlClientEventSource Log = new();

    private SqlClientEventSource()
    {
    }

    /// <summary>
    /// The keywords that let a tool take one family of Verbose events without the others.
    /// </summary>
    public static class Keywords
    {
        /// <summary>The per-command trace: <c>CommandStart</c> and <c>CommandStop</c>.</summary>
        public const EventKeywords Commands = (EventKeywords)0x1;
    }

    /// <summary>
    /// Records what a command threw and returns false, so the exception filter that calls it catches
    /// nothing; the command writes its end from the <c>finally</c> of the same <c>try</c>.
    /// </summary>
    /// <param name="exception">What the command threw.</param>
    /// <param name="captured">Receives <paramref name="exception"/>.</param>
    /// <returns>False, always.</returns>
    public static bool CaptureFailure(Exception exception, out Exception captured)
    {
        captured = exception;
        return false;
    }

    /// <summary>
    /// Writes the start of a command.
    /// </summary>
    /// <param name="connection">The connection that runs the command.</param>
    /// <param name="parameterCount">The number of bound parameters.</param>
    /// <returns>Whether the start was written: only then does the command's end write its <c>CommandStop</c>.</returns>
    [NonEvent]
    public bool CommandStart(SqlConnection connection, int parameterCount)
    {
        if (!IsEnabled(EventLevel.Verbose, Keywords.Commands))
        {
            return false;
        }

        CommandStart(connection.Database, parameterCount);
        return true;
    }

    /// <summary>
    /// Writes the end of a command on every path: <c>CommandFailed</c> first for a failure, then
    /// <c>CommandStop</c> with the command's status when its start was written, as
    /// <c>System.Net.Http</c>'s <c>RequestStop</c>. <c>CommandFailed</c> is written either way.
    /// </summary>
    /// <param name="connection">The connection that ran the command.</param>
    /// <param name="startWritten">What <see cref="CommandStart(SqlConnection, int)"/> returned.</param>
    /// <param name="failure">What the command threw, or <see langword="null"/> when it returned its result.</param>
    /// <param name="rowCount">The rows the command returned; -1 when it failed.</param>
    /// <param name="affectedCount">The records it affected, -1 for a row-returning command or a failure.</param>
    /// <param name="startTimestamp">The timestamp taken when the command started.</param>
    [NonEvent]
    public void CommandEnded(SqlConnection connection, bool startWritten, Exception? failure, long rowCount, long affectedCount, long startTimestamp)
    {
        bool failed = failure is not null and not OperationCanceledException;
        bool stop = startWritten && IsEnabled(EventLevel.Verbose, Keywords.Commands);
        if (!stop && !(failed && IsEnabled(EventLevel.Error, EventKeywords.None)))
        {
            return;
        }

        double duration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (failed && IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            var coded = failure as SqlClientException;
            CommandFailed(
                connection.Database,
                coded?.Kind.ToString() ?? string.Empty,
                coded?.Code.ToString() ?? string.Empty,
                TypeName(failure!),
                duration);
        }

        if (stop)
        {
            string status = failure is null ? StatusSuccess : failed ? StatusError : StatusCancelled;
            CommandStop(connection.Database, status, rowCount, affectedCount, duration);
        }
    }

    /// <summary>
    /// Writes an observer hook that threw; the client swallows the failure.
    /// </summary>
    /// <param name="connection">The connection whose command the hook observed.</param>
    /// <param name="callback">The hook's name.</param>
    /// <param name="exception">The hook's failure.</param>
    [NonEvent]
    public void ObserverFailed(SqlConnection connection, string callback, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ObserverFailed(connection.Database, callback, TypeName(exception), exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Commands, Message = "SQL command on '{0}' started with {1} parameter(s).")]
    private void CommandStart(string database, int parameterCount)
        => WriteEvent(1, database, parameterCount);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Commands, Message = "SQL command on '{0}' ended {1}: {2} row(s), {3} affected, in {4} ms.")]
    private void CommandStop(string database, string status, long rowCount, long affectedCount, double durationMilliseconds)
        => WriteEvent(2, database, status, rowCount, affectedCount, durationMilliseconds);

    [Event(3, Level = EventLevel.Error, Message = "SQL command on '{0}' failed after {4} ms: kind '{1}', code '{2}', exception '{3}'.")]
    private void CommandFailed(string database, string errorKind, string code, string exceptionType, double durationMilliseconds)
        => WriteEvent(3, database, errorKind, code, exceptionType, durationMilliseconds);

    [Event(4, Level = EventLevel.Warning, Message = "The SQL client observer's {1} hook threw on a command on '{0}': {2}: {3}. The command's outcome is unchanged.")]
    private void ObserverFailed(string database, string callback, string exceptionType, string exceptionMessage)
        => WriteEvent(4, database, callback, exceptionType, exceptionMessage);

    private static string TypeName(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;
}
