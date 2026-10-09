using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Internal;

/// <summary>
/// The typed key-value client's diagnostics: each command's start, stop and coded failure, and the
/// failures of the application's <see cref="KeyValueClientObserver"/> hooks, which the client swallows.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.KeyValuePair.Client</c>; applications
/// forward it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The connection
/// itself is reported by the shared client core's source, <c>Assimalign.Cohesion.Database.Client</c>.
/// The ids, names and payloads are the SQL client's (<c>Assimalign.Cohesion.Database.Sql.Client</c>),
/// so one query reads both.
/// </para>
/// <para>
/// A command writes its database, parameter count, row and affected counts, error kind and wire
/// code; never its command text, keys or values (plan D8). A failure also writes the server's
/// message, which names a conflicting key in hexadecimal, so <c>CommandFailed</c> replaces the
/// hexadecimal form of the command's byte parameters in it. No counters: a process-wide count
/// updated per command would be a contention point (plan D6).
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.KeyValuePair.Client")]
internal sealed class KeyValueClientEventSource : EventSource
{
    /// <summary>The text that stands for a key's or a value's bytes in a written failure message.</summary>
    internal const string RedactedValue = "<redacted>";

    public static readonly KeyValueClientEventSource Log = new();

    private KeyValueClientEventSource()
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
    /// Writes the start of a command.
    /// </summary>
    /// <param name="connection">The connection that runs the command.</param>
    /// <param name="parameterCount">The number of bound parameters.</param>
    [NonEvent]
    public void CommandStart(KeyValueConnection connection, int parameterCount)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Commands))
        {
            CommandStart(connection.Database, parameterCount);
        }
    }

    /// <summary>
    /// Writes the successful end of a command.
    /// </summary>
    /// <param name="connection">The connection that ran the command.</param>
    /// <param name="rowCount">The number of rows the command returned.</param>
    /// <param name="affectedCount">The number of entries the command affected, or -1 for a row-returning command.</param>
    /// <param name="startTimestamp">The timestamp taken when the command started.</param>
    [NonEvent]
    public void CommandStop(KeyValueConnection connection, long rowCount, long affectedCount, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Commands))
        {
            CommandStop(connection.Database, rowCount, affectedCount, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes a command that failed with a coded error.
    /// </summary>
    /// <param name="connection">The connection that ran the command.</param>
    /// <param name="exception">The failure the command throws.</param>
    /// <param name="parameters">The command's bound parameters, whose bytes are removed from the written message.</param>
    /// <param name="startTimestamp">The timestamp taken when the command started.</param>
    [NonEvent]
    public void CommandFailed(KeyValueConnection connection, KeyValueClientException exception,
        IReadOnlyDictionary<string, object?>? parameters, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            CommandFailed(
                connection.Database,
                exception.Kind.ToString(),
                exception.Code.ToString(),
                RedactParameters(exception.Message, parameters),
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes an observer hook that threw; the client swallows the failure.
    /// </summary>
    /// <param name="connection">The connection whose command the hook observed.</param>
    /// <param name="callback">The hook's name.</param>
    /// <param name="exception">The hook's failure.</param>
    [NonEvent]
    public void ObserverFailed(KeyValueConnection connection, string callback, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ObserverFailed(connection.Database, callback, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Commands, Message = "Key-value command on '{0}' started with {1} parameter(s).")]
    private void CommandStart(string database, int parameterCount)
        => WriteEvent(1, database, parameterCount);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Commands, Message = "Key-value command on '{0}' completed: {1} row(s), {2} affected, in {3} ms.")]
    private void CommandStop(string database, long rowCount, long affectedCount, double durationMilliseconds)
        => WriteEvent(2, database, rowCount, affectedCount, durationMilliseconds);

    [Event(3, Level = EventLevel.Error, Message = "Key-value command on '{0}' failed ({1}, {2}): {3}. After {4} ms.")]
    private void CommandFailed(string database, string errorKind, string code, string exceptionMessage, double durationMilliseconds)
        => WriteEvent(3, database, errorKind, code, exceptionMessage, durationMilliseconds);

    [Event(4, Level = EventLevel.Warning, Message = "The key-value client observer's {1} hook threw on a command on '{0}': {2}: {3}. The command's outcome is unchanged.")]
    private void ObserverFailed(string database, string callback, string exceptionType, string exceptionMessage)
        => WriteEvent(4, database, callback, exceptionType, exceptionMessage);

    /// <summary>
    /// Removes a command's key and value bytes from a failure's message. The server names a key in
    /// hexadecimal in some of its messages (a write-write conflict names the key it lost), and keys
    /// and values may be user data (plan D8), so the hexadecimal form of every byte parameter the
    /// command bound is replaced. A parameter longer than half the message cannot occur in it and is
    /// skipped, so a large value is never encoded.
    /// </summary>
    /// <param name="message">The failure's message.</param>
    /// <param name="parameters">The command's bound parameters, or <see langword="null"/>.</param>
    /// <returns>The message without the parameters' bytes.</returns>
    private static string RedactParameters(string message, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null)
        {
            return message;
        }

        foreach (object? value in parameters.Values)
        {
            if (value is byte[] { Length: > 0 } bytes && bytes.Length <= message.Length / 2)
            {
                message = message.Replace(Convert.ToHexString(bytes), RedactedValue, StringComparison.OrdinalIgnoreCase);
            }
        }

        return message;
    }
}
