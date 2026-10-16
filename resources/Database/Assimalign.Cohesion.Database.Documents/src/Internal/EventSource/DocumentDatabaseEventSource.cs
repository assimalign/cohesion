using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>
/// The document model's diagnostics: its wire server's accept loop and the lifecycle of each server
/// session (accepted, rejected, refused at the handshake, timed out, closed, terminated by a
/// protocol violation or an unexpected fault, aborted at shutdown), the index recovery a reopened
/// database runs, the wire statements that fail to parse, and the server-session counters.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Documents</c>; applications forward
/// it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The SQL, Key-value and
/// Blob servers share the lifecycle names. Documents preserves its recovery events at IDs 1-2,
/// so its lifecycle events use IDs 3-11. Repeatable warnings and errors include a suppressed count.
/// </para>
/// <para>
/// A statement's own outcome is the root source's (<c>Assimalign.Cohesion.Database</c>), so the
/// server does not repeat it; a wire statement that fails to parse never reaches typed execution, which is
/// why this source writes <c>StatementParseFailed</c> for it. Every write sits
/// behind <see cref="EventSource.IsEnabled(EventLevel, EventKeywords)"/>, and every argument that
/// allocates is computed inside that check. The counters' backing fields are maintained whether or
/// not anyone listens, on the accept and close transitions only, never per frame or per row.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Documents")]
internal sealed class DocumentDatabaseEventSource : EventSource
{
    public static readonly DocumentDatabaseEventSource Log = new();

    // The longest name (database, principal) and text (a refusal's detail, a violation) a payload
    // carries. Each can quote what a peer sent, before authentication too,
    // and a frame may hold 16 MB, so a longer value is cut and marked (event-source.md rule 11:
    // bounded payloads).
    private const int MaxNameLength = 256;
    private const int MaxTextLength = 1024;

    private PollingCounter? _currentSessionsCounter;
    private PollingCounter? _totalSessionsCounter;
    private PollingCounter? _rejectedSessionsCounter;
    private long _currentSessions;
    private long _totalSessions;
    private long _rejectedSessions;


    private readonly long[] _lastWarnings = new long[13];
    private readonly int[] _suppressedWarnings = new int[13];

    [NonEvent]
    private bool ShouldReport(int id, out int suppressedCount)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastWarnings[id]);
        if ((last != 0 && now - last < Stopwatch.Frequency) ||
            Interlocked.CompareExchange(ref _lastWarnings[id], now, last) != last)
        {
            Interlocked.Increment(ref _suppressedWarnings[id]);
            suppressedCount = 0;
            return false;
        }
        suppressedCount = Interlocked.Exchange(ref _suppressedWarnings[id], 0);
        return true;
    }

    private DocumentDatabaseEventSource()
    {
    }

    /// <summary>
    /// The source's keywords: explicit bits below the reserved top 16.
    /// </summary>
    public static class Keywords
    {
        /// <summary>The per-session lifecycle detail: accepted and closed sessions.</summary>
        public const EventKeywords Sessions = (EventKeywords)0x1;
    }

    /// <summary>
    /// Why a server session ended, as <c>SessionClosed</c> reports it.
    /// </summary>
    internal static class CloseReason
    {
        /// <summary>The peer ended its stream at a frame boundary.</summary>
        public const string PeerClosed = "PeerClosed";

        /// <summary>The peer sent Terminate.</summary>
        public const string Terminated = "Terminated";

        /// <summary>The session idled past the server's idle timeout.</summary>
        public const string IdleTimeout = "IdleTimeout";

        /// <summary>The server's graceful drain closed the idle session at a frame boundary.</summary>
        public const string Shutdown = "Shutdown";

        /// <summary>The handshake did not complete within the authentication timeout.</summary>
        public const string HandshakeTimedOut = "HandshakeTimedOut";

        /// <summary>The handshake was refused with a coded error frame.</summary>
        public const string HandshakeRefused = "HandshakeRefused";

        /// <summary>A framing or message-order violation terminated the session.</summary>
        public const string ProtocolViolation = "ProtocolViolation";

        /// <summary>The session was aborted, its connection closed, or the stop arrived mid-frame.</summary>
        public const string Cancelled = "Cancelled";

        /// <summary>The connection was aborted under the session.</summary>
        public const string ConnectionAborted = "ConnectionAborted";

        /// <summary>The transport failed under the session.</summary>
        public const string TransportFailed = "TransportFailed";

        /// <summary>An unexpected server error terminated the session.</summary>
        public const string Faulted = "Faulted";

        /// <summary>The pump ended without a classified outcome (an out-of-memory failure, which it does not catch).</summary>
        public const string Unknown = "Unknown";
    }

    /// <summary>
    /// Why the accept loop turned a connection away, as <c>SessionRejected</c> reports it.
    /// </summary>
    internal static class RejectReason
    {
        /// <summary>The server held its maximum of sessions.</summary>
        public const string SessionLimit = "SessionLimit";
    }

    /// <summary>The server sessions accepted and not yet completed.</summary>
    internal long CurrentServerSessions => Volatile.Read(ref _currentSessions);

    /// <summary>The server sessions accepted since the process started.</summary>
    internal long TotalServerSessions => Volatile.Read(ref _totalSessions);

    /// <summary>The connections the accept loop rejected since the process started.</summary>
    internal long TotalRejectedSessions => Volatile.Read(ref _rejectedSessions);

    /// <summary>
    /// Reads the timestamp a session's <c>SessionClosed</c> duration starts from, only while that
    /// event is enabled.
    /// </summary>
    /// <returns>A <see cref="Stopwatch"/> timestamp, or zero while the event is disabled.</returns>
    [NonEvent]
    public long SessionTimestamp()
        => IsEnabled(EventLevel.Verbose, Keywords.Sessions) ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Writes that the accept loop made a connection a server session.
    /// </summary>
    /// <param name="engine">The engine the server fronts.</param>
    /// <param name="session">The accepted session.</param>
    /// <param name="activeSessions">The sessions the server holds, this one included, as the accept loop counted them.</param>
    [NonEvent]
    public void SessionAccepted(DatabaseEngine engine, DatabaseServerSession session, int activeSessions)
    {
        Interlocked.Increment(ref _currentSessions);
        Interlocked.Increment(ref _totalSessions);

        if (IsEnabled(EventLevel.Verbose, Keywords.Sessions))
        {
            SessionAccepted(engine.Name, session.Id, activeSessions);
        }
    }

    /// <summary>
    /// Writes that the accept loop turned a connection away before it became a session.
    /// </summary>
    /// <param name="engine">The engine the server fronts.</param>
    /// <param name="reason">A <see cref="RejectReason"/> value.</param>
    /// <param name="activeSessions">The sessions the server held.</param>
    /// <param name="maxSessions">The server's session limit.</param>
    [NonEvent]
    public void SessionRejected(DatabaseEngine engine, string reason, int activeSessions, int maxSessions)
    {
        Interlocked.Increment(ref _rejectedSessions);

        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && ShouldReport(4, out int suppressedCount))
        {
            SessionRejected(engine.Name, reason, activeSessions, maxSessions, suppressedCount);
        }
    }

    /// <summary>
    /// Writes that the handshake refused a session with a coded error frame.
    /// </summary>
    /// <param name="session">The refused session.</param>
    /// <param name="database">The database the startup named; empty before the startup was read. Written cut to 256 characters.</param>
    /// <param name="principal">The principal the startup claimed; empty before the startup was read. Written cut to 256 characters.</param>
    /// <param name="code">The error frame's code.</param>
    /// <param name="detail">The error frame's message. Written cut to 1024 characters.</param>
    [NonEvent]
    public void HandshakeRefused(DatabaseServerSession session, string database, string principal, ProtocolErrorCode code, string detail)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && ShouldReport(5, out int suppressedCount))
        {
            HandshakeRefused(session.Id, Bound(database, MaxNameLength), Bound(principal, MaxNameLength), code.ToString(), Bound(detail, MaxTextLength), suppressedCount);
        }
    }

    /// <summary>
    /// Writes that a session did not complete its handshake within the authentication timeout and
    /// was dropped.
    /// </summary>
    /// <param name="session">The dropped session.</param>
    /// <param name="timeout">The server's authentication timeout.</param>
    [NonEvent]
    public void HandshakeTimedOut(DatabaseServerSession session, TimeSpan timeout)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && ShouldReport(6, out int suppressedCount))
        {
            HandshakeTimedOut(session.Id, timeout.TotalMilliseconds, suppressedCount);
        }
    }

    /// <summary>
    /// Writes that a server session completed and left the server.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="reason">A <see cref="CloseReason"/> value.</param>
    /// <param name="acceptedTimestamp">The session's <see cref="SessionTimestamp"/>; zero when it was accepted while the event was disabled, which writes a zero duration.</param>
    [NonEvent]
    public void SessionClosed(DatabaseServerSession session, string reason, long acceptedTimestamp)
    {
        Interlocked.Decrement(ref _currentSessions);

        if (IsEnabled(EventLevel.Verbose, Keywords.Sessions))
        {
            SessionClosed(session.Id, reason, acceptedTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(acceptedTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that a framing or message-order violation terminated a session.
    /// </summary>
    /// <param name="session">The terminated session.</param>
    /// <param name="message">The violation, as the error frame states it. Written cut to 1024 characters.</param>
    [NonEvent]
    public void SessionProtocolViolation(DatabaseServerSession session, string message)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && ShouldReport(8, out int suppressedCount))
        {
            SessionProtocolViolation(session.Id, Bound(message, MaxTextLength), suppressedCount);
        }
    }

    /// <summary>
    /// Writes that an unexpected server error terminated a session; the peer receives an internal
    /// error frame only.
    /// </summary>
    /// <param name="session">The terminated session.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void SessionFaulted(DatabaseServerSession session, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None) && ShouldReport(9, out int suppressedCount))
        {
            SessionFaulted(session.Id, exception.GetType().FullName ?? exception.GetType().Name, exception.Message, suppressedCount);
        }
    }

    /// <summary>
    /// Writes that a session failed to release a resource while it closed; the close continues.
    /// </summary>
    /// <param name="session">The closing session.</param>
    /// <param name="exception">The failure the close swallowed.</param>
    [NonEvent]
    public void SessionCleanupFailed(DatabaseServerSession session, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && ShouldReport(10, out int suppressedCount))
        {
            SessionCleanupFailed(session.Id, exception.GetType().FullName ?? exception.GetType().Name, exception.Message, suppressedCount);
        }
    }

    /// <summary>
    /// Writes that a stopping server aborted the sessions its drain budget did not close.
    /// </summary>
    /// <param name="engine">The engine the server fronts.</param>
    /// <param name="sessions">The sessions the server aborted.</param>
    /// <param name="drainTimeout">The server's drain budget.</param>
    [NonEvent]
    public void SessionsAborted(DatabaseEngine engine, int sessions, TimeSpan drainTimeout)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && ShouldReport(11, out int suppressedCount))
        {
            SessionsAborted(engine.Name, sessions, drainTimeout.TotalMilliseconds, suppressedCount);
        }
    }

    /// <summary>
    /// Writes that a reopened database starts recovering its indexes from the writers the
    /// journal's analysis found aborted.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="abortedWriters">The aborted writers whose index entries the recovery removes.</param>
    /// <returns>The timestamp <see cref="IndexRecoveryStop"/> measures from, or zero while the event is disabled.</returns>
    [NonEvent]
    public long IndexRecoveryStart(DatabaseName database, int abortedWriters)
    {
        if (!IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            return 0;
        }

        IndexRecoveryStart(database.ToString(), abortedWriters);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Writes that a reopened database's index recovery ended, whether it recovered the indexes or
    /// threw (the root's failed open reports the failure), so the activity its start opened always
    /// closes; written only for a recovery whose start was written.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="succeeded">False when the recovery threw: the stop is written with <c>Error</c>.</param>
    /// <param name="startTimestamp">The timestamp <see cref="IndexRecoveryStart(DatabaseName, int)"/> returned.</param>
    [NonEvent]
    public void IndexRecoveryStop(DatabaseName database, bool succeeded, long startTimestamp)
    {
        if (startTimestamp != 0 && IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            IndexRecoveryStop(database.ToString(), succeeded ? "Success" : "Error", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that a statement received over the wire failed to parse or validate into its request,
    /// before the root session saw it.
    /// </summary>
    /// <param name="session">The server session that received the statement.</param>
    /// <param name="database">The session's database.</param>
    /// <param name="exception">
    /// The parse or validation failure; it propagates unchanged. Written by its code (an offline
    /// refusal's; empty otherwise) and its type only: a parse error quotes the token it stopped at
    /// (the area's failure rule).
    /// </param>
    [NonEvent]
    public void StatementParseFailed(DatabaseServerSession session, DatabaseName database, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None) && ShouldReport(12, out int suppressedCount))
        {
            StatementParseFailed(
                session.Id,
                database.ToString(),
                exception is DatabaseOfflineException offline ? offline.Code : string.Empty,
                exception.GetType().FullName ?? exception.GetType().Name, suppressedCount);
        }
    }

    [Event(3, Level = EventLevel.Verbose, Keywords = Keywords.Sessions, Message = "The server of engine {0} accepted session {1}; {2} session(s) active.")]
    private void SessionAccepted(string engineName, Guid sessionId, int activeSessions)
        => WriteEvent(3, engineName, sessionId, activeSessions);

    [Event(4, Level = EventLevel.Warning, Message = "The server of engine {0} rejected a connection ({1}): {2} of {3} session(s) active.")]
    private void SessionRejected(string engineName, string reason, int activeSessions, int maxSessions, int suppressedCount)
        => WriteEvent(4, engineName, reason, activeSessions, maxSessions, suppressedCount);

    [Event(5, Level = EventLevel.Warning, Message = "Session {0} was refused at its handshake for database '{1}' and principal '{2}' with {3}: {4}")]
    private void HandshakeRefused(Guid sessionId, string database, string principal, string code, string detail, int suppressedCount)
        => WriteEvent(5, sessionId, database, principal, code, detail, suppressedCount);

    [Event(6, Level = EventLevel.Warning, Message = "Session {0} did not complete its handshake within {1} ms and was dropped.")]
    private void HandshakeTimedOut(Guid sessionId, double timeoutMilliseconds, int suppressedCount)
        => WriteEvent(6, sessionId, timeoutMilliseconds, suppressedCount);

    [Event(7, Level = EventLevel.Verbose, Keywords = Keywords.Sessions, Message = "Session {0} closed ({1}) after {2} ms.")]
    private void SessionClosed(Guid sessionId, string reason, double durationMilliseconds)
        => WriteEvent(7, sessionId, reason, durationMilliseconds);

    [Event(8, Level = EventLevel.Warning, Message = "Session {0} violated the protocol and was terminated: {1}")]
    private void SessionProtocolViolation(Guid sessionId, string exceptionMessage, int suppressedCount)
        => WriteEvent(8, sessionId, exceptionMessage, suppressedCount);

    [Event(9, Level = EventLevel.Error, Message = "Session {0} was terminated by an unexpected server error: {1}: {2}")]
    private void SessionFaulted(Guid sessionId, string exceptionType, string exceptionMessage, int suppressedCount)
        => WriteEvent(9, sessionId, exceptionType, exceptionMessage, suppressedCount);

    [Event(10, Level = EventLevel.Warning, Message = "Session {0} failed to release a resource while closing: {1}: {2}")]
    private void SessionCleanupFailed(Guid sessionId, string exceptionType, string exceptionMessage, int suppressedCount)
        => WriteEvent(10, sessionId, exceptionType, exceptionMessage, suppressedCount);

    [Event(11, Level = EventLevel.Warning, Message = "The server of engine {0} aborted {1} session(s) its {2} ms drain did not close.")]
    private void SessionsAborted(string engineName, int sessions, double drainTimeoutMilliseconds, int suppressedCount)
        => WriteEvent(11, engineName, sessions, drainTimeoutMilliseconds, suppressedCount);

    [Event(1, Level = EventLevel.Informational, Message = "Database '{0}' started recovering its indexes from {1} aborted writer(s).")]
    private void IndexRecoveryStart(string database, int abortedWriters)
        => WriteEvent(1, database, abortedWriters);

    [Event(2, Level = EventLevel.Informational, Message = "Database '{0}' ended its index recovery {1} after {2} ms.")]
    private void IndexRecoveryStop(string database, string status, double durationMilliseconds)
        => WriteEvent(2, database, status, durationMilliseconds);

    [Event(12, Level = EventLevel.Error, Message = "A statement session {0} received for database '{1}' failed to parse: code '{2}', exception '{3}'.")]
    private void StatementParseFailed(Guid sessionId, string database, string code, string exceptionType, int suppressedCount)
        => WriteEvent(12, sessionId, database, code, exceptionType, suppressedCount);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // Created on the first enable command and kept for the source's lifetime. The backing
        // fields are maintained whether or not anyone listens, so a tool that attaches late still
        // reads exact values.
        _currentSessionsCounter ??= new PollingCounter("current-server-sessions", this, () => Volatile.Read(ref _currentSessions))
        {
            DisplayName = "Current Server Sessions",
        };
        _totalSessionsCounter ??= new PollingCounter("total-server-sessions", this, () => Volatile.Read(ref _totalSessions))
        {
            DisplayName = "Total Server Sessions",
        };
        _rejectedSessionsCounter ??= new PollingCounter("total-rejected-sessions", this, () => Volatile.Read(ref _rejectedSessions))
        {
            DisplayName = "Total Rejected Sessions",
        };
    }

    // Cuts a peer-supplied string to its payload bound and marks the cut; never splits a surrogate
    // pair. Called only inside an IsEnabled check.
    private static string Bound(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return string.Concat(value.AsSpan(0, length), "...");
    }
}
