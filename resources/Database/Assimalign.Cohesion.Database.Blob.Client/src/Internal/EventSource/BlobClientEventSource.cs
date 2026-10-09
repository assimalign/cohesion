using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client.Internal;

/// <summary>
/// The Blob client's diagnostics: each transfer's start, stop and coded failure, and a listing
/// failure the client swallows during cleanup.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Blob.Client</c>; applications forward
/// it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The connection itself is
/// reported by the shared client core's source, <c>Assimalign.Cohesion.Database.Client</c>.
/// </para>
/// <para>
/// <c>operation</c> names the public member: <c>Upload</c>, <c>Download</c>, <c>Delete</c>,
/// <c>GetProperties</c> or <c>List</c>; <c>bytes</c> is the content an upload sent or a download
/// received, and zero for the metadata operations. A download stops when its last chunk is verified,
/// which can be after <see cref="BlobConnection.DownloadAsync"/> returned its stream. Container names
/// are identifiers and are written; blob names may be user data and are never written, nor is any
/// content (plan D8). The server names the blob in some of its failure messages, so
/// <c>TransferFailed</c> replaces the transfer's blob name in the message it writes. No counters
/// (plan D6).
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Blob.Client")]
internal sealed class BlobClientEventSource : EventSource
{
    /// <summary>The text that stands for a blob's name in a written failure message.</summary>
    internal const string RedactedName = "<redacted>";

    public static readonly BlobClientEventSource Log = new();

    private BlobClientEventSource()
    {
    }

    /// <summary>
    /// The keywords that let a tool take one family of Verbose events without the others.
    /// </summary>
    public static class Keywords
    {
        /// <summary>The per-transfer trace: <c>TransferStart</c> and <c>TransferStop</c>.</summary>
        public const EventKeywords Transfers = (EventKeywords)0x1;
    }

    /// <summary>
    /// Takes a timestamp for a transfer's duration, only while a listener takes the source.
    /// </summary>
    /// <returns>The timestamp, or zero when nobody listens.</returns>
    [NonEvent]
    public long GetTimestamp()
        => IsEnabled() ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Writes the start of a transfer.
    /// </summary>
    /// <param name="connection">The connection that runs the transfer.</param>
    /// <param name="operation">The public member that runs it.</param>
    /// <param name="container">The container it addresses.</param>
    [NonEvent]
    public void TransferStart(BlobConnection connection, string operation, string? container)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transfers))
        {
            TransferStart(connection.Database, operation, container ?? string.Empty);
        }
    }

    /// <summary>
    /// Writes the successful end of a transfer.
    /// </summary>
    /// <param name="connection">The connection that ran the transfer.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="container">The container it addressed.</param>
    /// <param name="bytes">The content bytes it sent or received; zero for a metadata operation.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the transfer started.</param>
    [NonEvent]
    public void TransferStop(BlobConnection connection, string operation, string? container, long bytes, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transfers))
        {
            TransferStop(connection.Database, operation, container ?? string.Empty, bytes, GetElapsedMilliseconds(startTimestamp));
        }
    }

    /// <summary>
    /// Writes a transfer that failed.
    /// </summary>
    /// <param name="connection">The connection that ran the transfer.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="container">The container it addressed.</param>
    /// <param name="name">The blob it addressed, removed from the written message; <see langword="null"/> for a listing.</param>
    /// <param name="code">The wire code of the failure.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the transfer started.</param>
    [NonEvent]
    public void TransferFailed(BlobConnection connection, string operation, string? container, string? name, ProtocolErrorCode code, Exception exception, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            TransferFailed(connection.Database, operation, container ?? string.Empty, code.ToString(), RedactName(exception.Message, name), GetElapsedMilliseconds(startTimestamp));
        }
    }

    /// <summary>
    /// Writes a listing failure the enumeration's cleanup swallowed, one its consumer never saw.
    /// </summary>
    /// <param name="connection">The connection that ran the listing.</param>
    /// <param name="container">The listed container.</param>
    /// <param name="exception">The swallowed failure.</param>
    [NonEvent]
    public void ListCleanupFailed(BlobConnection connection, string? container, Exception exception)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ListCleanupFailed(connection.Database, container ?? string.Empty, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Transfers, Message = "Blob {1} on '{0}', container '{2}', started.")]
    private void TransferStart(string database, string operation, string container)
        => WriteEvent(1, database, operation, container);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Transfers, Message = "Blob {1} on '{0}', container '{2}', completed: {3} byte(s) in {4} ms.")]
    private void TransferStop(string database, string operation, string container, long bytes, double durationMilliseconds)
        => WriteEvent(2, database, operation, container, bytes, durationMilliseconds);

    [Event(3, Level = EventLevel.Error, Message = "Blob {1} on '{0}', container '{2}', failed ({3}): {4}. After {5} ms.")]
    private void TransferFailed(string database, string operation, string container, string code, string exceptionMessage, double durationMilliseconds)
        => WriteEvent(3, database, operation, container, code, exceptionMessage, durationMilliseconds);

    [Event(4, Level = EventLevel.Verbose, Message = "The cleanup of a Blob listing on '{0}', container '{1}', swallowed a failure its consumer never saw: {2}: {3}")]
    private void ListCleanupFailed(string database, string container, string exceptionType, string exceptionMessage)
        => WriteEvent(4, database, container, exceptionType, exceptionMessage);

    private static double GetElapsedMilliseconds(long startTimestamp)
        => startTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>
    /// Removes a blob's name from a failure's message. The server names the blob in its own
    /// messages (<c>Blob '…' does not exist.</c>, <c>Blob '…' already exists.</c>), and a name may
    /// be user data (plan D8), so every occurrence is replaced; a name that also occurs in the
    /// message's fixed text is replaced there too, which costs readability, never the name.
    /// </summary>
    /// <param name="message">The failure's message.</param>
    /// <param name="name">The blob's name, or <see langword="null"/>.</param>
    /// <returns>The message without the name.</returns>
    private static string RedactName(string message, string? name)
        => string.IsNullOrEmpty(name) ? message : message.Replace(name, RedactedName, StringComparison.Ordinal);
}
