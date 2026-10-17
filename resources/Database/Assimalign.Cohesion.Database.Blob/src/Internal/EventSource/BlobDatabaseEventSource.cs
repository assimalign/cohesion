using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Internal;

/// <summary>
/// The blob model's diagnostics: its wire server's accept loop and the lifecycle of each server
/// session (accepted, rejected, refused at the handshake, timed out, closed, terminated by a
/// protocol violation or an unexpected fault, aborted at shutdown), the server's refusals of one
/// database or of the whole engine, the exchange failures it absorbs, and the server-session
/// counters.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Blob</c>; applications forward
/// it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The SQL, Key-value and
/// Graph servers write events 1-9 with the same ids, names and payloads from their own sources
/// (database event-sources plan, D2), so one provider list and one log query cover all four.
/// </para>
/// <para>
/// The model has no statements: a container operation's transaction is the Transactions source's,
/// and its wire outcome is this source's <c>TransferFailed</c> when it fails. No payload carries a
/// blob name (it may be user data; plan D8 and owner question Q3); container names are
/// identifiers. The engine's messages quote a blob's name (<c>Blob 'x' does not exist.</c>), so
/// <c>TransferFailed</c> writes the failure's wire code and exception type only, never its message
/// (the area's failure rule, <c>docs/resources/Database/DESIGN.md</c>). A string a peer sent is cut
/// to a bound (256 characters for a name, 1024 for a message). Every write sits
/// behind <see cref="EventSource.IsEnabled(EventLevel, EventKeywords)"/>, and every argument that
/// allocates is computed inside that check. The counters' backing fields are maintained whether or
/// not anyone listens, on the accept and close transitions only, never per frame or per row.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Blob")]
internal sealed class BlobDatabaseEventSource : EventSource
{
    public static readonly BlobDatabaseEventSource Log = new();

    // The longest name (database, principal, container) and text (a refusal's detail, a violation)
    // a payload carries. Each can quote what a peer sent, before
    // authentication too, and a frame may hold 16 MB, so a longer value is cut and marked
    // (event-source.md rule 11: bounded payloads).
    private const int MaxNameLength = 256;
    private const int MaxTextLength = 1024;

    private PollingCounter? _currentSessionsCounter;
    private PollingCounter? _totalSessionsCounter;
    private PollingCounter? _rejectedSessionsCounter;
    private long _currentSessions;
    private long _totalSessions;
    private long _rejectedSessions;

    private BlobDatabaseEventSource()
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

        /// <summary>An exchange was refused for the engine's or the database's state, which ends the session.</summary>
        public const string ExchangeRefused = "ExchangeRefused";

        /// <summary>An exchange failed, which ends the session.</summary>
        public const string ExchangeFailed = "ExchangeFailed";
    }

    /// <summary>
    /// Why the accept loop turned a connection away, as <c>SessionRejected</c> reports it.
    /// </summary>
    internal static class RejectReason
    {
        /// <summary>The server held its maximum of sessions.</summary>
        public const string SessionLimit = "SessionLimit";

        /// <summary>The engine refuses every database: it is disposed or failed as a whole.</summary>
        public const string EngineRefused = "EngineRefused";
    }

    /// <summary>
    /// Where the server refused a database or its engine, as <c>DatabaseRefused</c> and
    /// <c>EngineRefused</c> report it.
    /// </summary>
    internal static class RefusalPhase
    {
        /// <summary>The accept loop, before the connection became a session.</summary>
        public const string Accept = "Accept";

        /// <summary>The session's handshake.</summary>
        public const string Handshake = "Handshake";

        /// <summary>An exchange of a ready session.</summary>
        public const string Exchange = "Exchange";
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

        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            SessionRejected(engine.Name, reason, activeSessions, maxSessions);
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
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            HandshakeRefused(session.Id, Bound(database, MaxNameLength), Bound(principal, MaxNameLength), code.ToString(), Bound(detail, MaxTextLength));
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
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            HandshakeTimedOut(session.Id, timeout.TotalMilliseconds);
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
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            SessionProtocolViolation(session.Id, Bound(message, MaxTextLength));
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
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            SessionFaulted(session.Id, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
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
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            SessionCleanupFailed(session.Id, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
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
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            SessionsAborted(engine.Name, sessions, drainTimeout.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that the server refused one database while a background worker of its engine keeps
    /// failing on it (owner decision 42; the refusal's message leads with <c>COHDBB003</c>).
    /// </summary>
    /// <param name="session">The refused session.</param>
    /// <param name="database">The refused database.</param>
    /// <param name="phase">A <see cref="RefusalPhase"/> value.</param>
    /// <param name="detail">The refusal, as the error frame states it. Written cut to 1024 characters.</param>
    [NonEvent]
    public void DatabaseRefused(DatabaseServerSession session, DatabaseName database, string phase, string detail)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            DatabaseRefused(session.Id, Bound(database.ToString(), MaxNameLength), phase, Bound(detail, MaxTextLength));
        }
    }

    /// <summary>
    /// Writes that the server refused work for every database because its engine is disposed or
    /// failed as a whole; a refused start is the root source's <c>ServerStartFailed</c>.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="phase">A <see cref="RefusalPhase"/> value.</param>
    /// <param name="state">The engine's state, as the refusal read it.</param>
    [NonEvent]
    public void EngineRefused(DatabaseEngine engine, string phase, EngineState state)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            EngineRefused(engine.Name, phase, state.ToString());
        }
    }

    /// <summary>
    /// Writes that a failed exchange could not abort the session's host-opened transaction; the
    /// transaction stays faulted and the teardown retries its rollback.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="exception">The abort's failure, which the session swallows.</param>
    [NonEvent]
    public void HostTransactionAbortFailed(DatabaseServerSession session, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            HostTransactionAbortFailed(session.Id, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    /// <summary>
    /// Writes that a container operation failed: an exchange the server ended the session over, or
    /// a stream's completion failure swallowed after its read already failed. Written by the wire
    /// code and the exception's type only: the engine's messages quote the blob's name.
    /// </summary>
    /// <param name="session">The session; null for a stream, which serves in-process callers too and knows no session.</param>
    /// <param name="container">The operation's container; empty when it is not known. Written cut to 256 characters.</param>
    /// <param name="code">The code of the error frame the session sent; null for a stream, which sends none.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void TransferFailed(DatabaseServerSession? session, string container, ProtocolErrorCode? code, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            TransferFailed(
                session?.Id ?? Guid.Empty,
                Bound(container, MaxNameLength),
                code?.ToString() ?? string.Empty,
                exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Sessions, Message = "The server of engine {0} accepted session {1}; {2} session(s) active.")]
    private void SessionAccepted(string engineName, Guid sessionId, int activeSessions)
        => WriteEvent(1, engineName, sessionId, activeSessions);

    [Event(2, Level = EventLevel.Warning, Message = "The server of engine {0} rejected a connection ({1}): {2} of {3} session(s) active.")]
    private void SessionRejected(string engineName, string reason, int activeSessions, int maxSessions)
        => WriteEvent(2, engineName, reason, activeSessions, maxSessions);

    [Event(3, Level = EventLevel.Warning, Message = "Session {0} was refused at its handshake for database '{1}' and principal '{2}' with {3}: {4}")]
    private void HandshakeRefused(Guid sessionId, string database, string principal, string code, string detail)
        => WriteEvent(3, sessionId, database, principal, code, detail);

    [Event(4, Level = EventLevel.Warning, Message = "Session {0} did not complete its handshake within {1} ms and was dropped.")]
    private void HandshakeTimedOut(Guid sessionId, double timeoutMilliseconds)
        => WriteEvent(4, sessionId, timeoutMilliseconds);

    [Event(5, Level = EventLevel.Verbose, Keywords = Keywords.Sessions, Message = "Session {0} closed ({1}) after {2} ms.")]
    private void SessionClosed(Guid sessionId, string reason, double durationMilliseconds)
        => WriteEvent(5, sessionId, reason, durationMilliseconds);

    [Event(6, Level = EventLevel.Warning, Message = "Session {0} violated the protocol and was terminated: {1}")]
    private void SessionProtocolViolation(Guid sessionId, string exceptionMessage)
        => WriteEvent(6, sessionId, exceptionMessage);

    [Event(7, Level = EventLevel.Error, Message = "Session {0} was terminated by an unexpected server error: {1}: {2}")]
    private void SessionFaulted(Guid sessionId, string exceptionType, string exceptionMessage)
        => WriteEvent(7, sessionId, exceptionType, exceptionMessage);

    [Event(8, Level = EventLevel.Warning, Message = "Session {0} failed to release a resource while closing: {1}: {2}")]
    private void SessionCleanupFailed(Guid sessionId, string exceptionType, string exceptionMessage)
        => WriteEvent(8, sessionId, exceptionType, exceptionMessage);

    [Event(9, Level = EventLevel.Warning, Message = "The server of engine {0} aborted {1} session(s) its {2} ms drain did not close.")]
    private void SessionsAborted(string engineName, int sessions, double drainTimeoutMilliseconds)
        => WriteEvent(9, engineName, sessions, drainTimeoutMilliseconds);

    [Event(10, Level = EventLevel.Warning, Message = "Session {0} was refused database '{1}' at its {2}: {3}")]
    private void DatabaseRefused(Guid sessionId, string database, string phase, string detail)
        => WriteEvent(10, sessionId, database, phase, detail);

    [Event(11, Level = EventLevel.Warning, Message = "The server of engine {0} refused work at its {1} because the engine is {2}.")]
    private void EngineRefused(string engineName, string phase, string state)
        => WriteEvent(11, engineName, phase, state);

    [Event(12, Level = EventLevel.Warning, Message = "Session {0} could not abort its host transaction after a failed exchange: {1}: {2}. The transaction stays faulted until its rollback.")]
    private void HostTransactionAbortFailed(Guid sessionId, string exceptionType, string exceptionMessage)
        => WriteEvent(12, sessionId, exceptionType, exceptionMessage);

    [Event(13, Level = EventLevel.Error, Message = "A transfer of session {0} on container '{1}' failed: code '{2}', exception '{3}'.")]
    private void TransferFailed(Guid sessionId, string container, string code, string exceptionType)
        => WriteEvent(13, sessionId, container, code, exceptionType);

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
