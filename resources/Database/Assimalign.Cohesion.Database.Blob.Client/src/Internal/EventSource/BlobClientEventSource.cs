using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client.Internal;

/// <summary>
/// The Blob client's diagnostics: each transfer's start, stop and failure, and a listing failure the
/// client swallows during cleanup.
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
/// received, and zero for the metadata operations and for a transfer that did not complete. A
/// download ends when its copy does, which can be after <see cref="BlobConnection.DownloadAsync"/>
/// returned its stream: <c>Success</c> once its last chunk is verified, <c>Cancelled</c> when its
/// caller disposes the stream early or cancels it, <c>Error</c> when it fails. Every
/// <c>TransferStart</c> is closed by one <c>TransferStop</c>, after <c>TransferFailed</c> for a
/// failure. Container names are identifiers and are written; blob names may be user data and are
/// never written, nor is any content (plan D8). A failure is written by its wire code and exception
/// type only, never a message: the server names the blob in some of its messages (the area's
/// failure rule, <c>docs/resources/Database/DESIGN.md</c>). No counters (plan D6).
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Blob.Client")]
internal sealed class BlobClientEventSource : EventSource
{
    /// <summary>The status of a transfer that completed.</summary>
    internal const string StatusSuccess = "Success";

    /// <summary>The status of a transfer that failed.</summary>
    internal const string StatusError = "Error";

    /// <summary>The status of a transfer its caller cancelled or abandoned.</summary>
    internal const string StatusCancelled = "Cancelled";

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
    /// Records what a transfer threw and returns false, so the exception filter that calls it catches
    /// nothing; the transfer writes its end from the <c>finally</c> of the same <c>try</c>.
    /// </summary>
    /// <param name="exception">What the transfer threw.</param>
    /// <param name="captured">Receives <paramref name="exception"/>.</param>
    /// <returns>False, always.</returns>
    public static bool CaptureFailure(Exception exception, out Exception captured)
    {
        captured = exception;
        return false;
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
    /// <returns>Whether the start was written: only then does the transfer's end write its <c>TransferStop</c>.</returns>
    [NonEvent]
    public bool TransferStart(BlobConnection connection, string operation, string? container)
    {
        if (!IsEnabled(EventLevel.Verbose, Keywords.Transfers))
        {
            return false;
        }

        TransferStart(connection.Database, operation, container ?? string.Empty);
        return true;
    }

    /// <summary>
    /// Writes the end of a transfer on every path, the code taken from the failure the caller sees:
    /// <c>TransferFailed</c> first for a failure, then <c>TransferStop</c> with the status when the
    /// start was written.
    /// </summary>
    /// <param name="connection">The connection that ran the transfer.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="container">The container it addressed.</param>
    /// <param name="startWritten">What <see cref="TransferStart(BlobConnection, string, string?)"/> returned.</param>
    /// <param name="failure">What the transfer threw, or <see langword="null"/> when it completed.</param>
    /// <param name="bytes">The content bytes it sent or received; zero for a metadata operation or a failure.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the transfer started.</param>
    [NonEvent]
    public void TransferEnded(BlobConnection connection, string operation, string? container, bool startWritten, Exception? failure, long bytes, long startTimestamp)
        => TransferEnded(connection, operation, container, startWritten, failure, failure is null ? null : GetCode(failure), bytes, startTimestamp);

    /// <summary>
    /// Writes the end of a transfer on every path: <c>TransferFailed</c> first for a failure, then
    /// <c>TransferStop</c> with the status when the start was written, as <c>System.Net.Http</c>'s
    /// <c>RequestStop</c>. <c>TransferFailed</c> is written either way. A cancellation is not a
    /// failure.
    /// </summary>
    /// <param name="connection">The connection that ran the transfer.</param>
    /// <param name="operation">The public member that ran it.</param>
    /// <param name="container">The container it addressed.</param>
    /// <param name="startWritten">What <see cref="TransferStart(BlobConnection, string, string?)"/> returned.</param>
    /// <param name="failure">What the transfer threw, or <see langword="null"/> when it completed.</param>
    /// <param name="code">The wire code of the failure, or <see langword="null"/> when it has none.</param>
    /// <param name="bytes">The content bytes it sent or received; zero for a metadata operation or a failure.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the transfer started.</param>
    [NonEvent]
    public void TransferEnded(BlobConnection connection, string operation, string? container, bool startWritten, Exception? failure, ProtocolErrorCode? code, long bytes, long startTimestamp)
    {
        bool failed = failure is not null and not OperationCanceledException;
        if (failed && IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            TransferFailed(
                connection.Database,
                operation,
                container ?? string.Empty,
                code?.ToString() ?? string.Empty,
                TypeName(failure!),
                GetElapsedMilliseconds(startTimestamp));
        }

        if (startWritten && IsEnabled(EventLevel.Verbose, Keywords.Transfers))
        {
            string status = failure is null ? StatusSuccess : failed ? StatusError : StatusCancelled;
            TransferStop(connection.Database, operation, container ?? string.Empty, status, bytes, GetElapsedMilliseconds(startTimestamp));
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
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ListCleanupFailed(connection.Database, container ?? string.Empty, GetCode(exception)?.ToString() ?? string.Empty, TypeName(exception));
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Transfers, Message = "Blob {1} on '{0}', container '{2}', started.")]
    private void TransferStart(string database, string operation, string container)
        => WriteEvent(1, database, operation, container);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Transfers, Message = "Blob {1} on '{0}', container '{2}', ended {3}: {4} byte(s) in {5} ms.")]
    private void TransferStop(string database, string operation, string container, string status, long bytes, double durationMilliseconds)
        => WriteEvent(2, database, operation, container, status, bytes, durationMilliseconds);

    [Event(3, Level = EventLevel.Error, Message = "Blob {1} on '{0}', container '{2}', failed after {5} ms: code '{3}', exception '{4}'.")]
    private void TransferFailed(string database, string operation, string container, string code, string exceptionType, double durationMilliseconds)
        => WriteEvent(3, database, operation, container, code, exceptionType, durationMilliseconds);

    [Event(4, Level = EventLevel.Warning, Message = "The cleanup of a Blob listing on '{0}', container '{1}', swallowed a failure its consumer never saw: code '{2}', exception '{3}'.")]
    private void ListCleanupFailed(string database, string container, string code, string exceptionType)
        => WriteEvent(4, database, container, code, exceptionType);

    // The wire code a failure carries: the Blob client's own, or the shared client's; none for a
    // failure that is not a wire error (a cancellation, a disposed or busy connection).
    private static ProtocolErrorCode? GetCode(Exception failure) => failure switch
    {
        BlobClientException blob => blob.Code,
        DatabaseClientException client => client.Code,
        _ => null,
    };

    private static string TypeName(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;

    private static double GetElapsedMilliseconds(long startTimestamp)
        => startTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
