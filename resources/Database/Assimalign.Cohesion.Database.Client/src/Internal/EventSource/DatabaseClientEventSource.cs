using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Internal;

/// <summary>
/// The shared client core's diagnostics: the lifecycle of each pooled wire connection (opened, failed
/// to open, broken, closed), the pool's rentals and returns, coded exchange failures, and the
/// connection counters.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Client</c>; applications forward it
/// into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every model client (Sql,
/// KeyValuePair, Graph, Blob) runs on <see cref="DatabaseClient"/>, so one source covers the
/// connections of all of them; each model client's own source reports its commands.
/// </para>
/// <para>
/// A connection that opened reports <c>ConnectionOpened</c> once and <c>ConnectionClosed</c> once,
/// behind a flag the first close exchanges, which keeps <c>current-connections</c> exact; a rental
/// is counted once when the pool hands the connection out and once when the rental is returned,
/// which keeps <c>current-rented-connections</c> exact. Endpoints and database names are written;
/// connection strings, credentials and statement text never are.
/// </para>
/// <para>
/// A statement-level server error can quote user data the core cannot recognize: a key, a blob name
/// or a fragment of the statement. <c>ExchangeFailed</c> therefore writes the failure's code and
/// exception type only, as every model client's failure event does (the area's failure rule,
/// <c>docs/resources/Database/DESIGN.md</c>). The lifecycle failures (a failed open, a broken
/// connection, a failed release) keep their message: the dial's names the endpoint, the handshake's
/// is a protocol refusal, and a transport or protocol fault's is the client's own text.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Client")]
internal sealed class DatabaseClientEventSource : EventSource
{
    public static readonly DatabaseClientEventSource Log = new();

    private PollingCounter? _currentConnectionsCounter;
    private PollingCounter? _currentRentedConnectionsCounter;
    private IncrementingPollingCounter? _connectionsOpenedRateCounter;
    private PollingCounter? _connectionFailuresCounter;
    private long _currentConnections;
    private long _currentRentedConnections;
    private long _totalConnectionsOpened;
    private long _totalConnectionFailures;

    private DatabaseClientEventSource()
    {
    }

    /// <summary>
    /// The keywords that let a tool take one family of Verbose events without the others.
    /// </summary>
    public static class Keywords
    {
        /// <summary>The pool's rentals and returns: <c>ConnectionRented</c> and <c>ConnectionReturned</c>.</summary>
        public const EventKeywords Pool = (EventKeywords)0x1;
    }

    /// <summary>Gets the connections opened and not yet closed.</summary>
    internal long CurrentConnections => Volatile.Read(ref _currentConnections);

    /// <summary>Gets the connections rented from a pool and not yet returned.</summary>
    internal long CurrentRentedConnections => Volatile.Read(ref _currentRentedConnections);

    /// <summary>Gets the connections opened since the process started.</summary>
    internal long TotalConnectionsOpened => Volatile.Read(ref _totalConnectionsOpened);

    /// <summary>Gets the connections that failed to open since the process started.</summary>
    internal long TotalConnectionFailures => Volatile.Read(ref _totalConnectionFailures);

    /// <summary>
    /// Takes a timestamp for an event's duration, only while a listener takes the source.
    /// </summary>
    /// <returns>The timestamp, or zero when nobody listens.</returns>
    [NonEvent]
    public long GetTimestamp()
        => IsEnabled() ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Takes a timestamp for the pool wait, only while a listener takes the pool trace.
    /// </summary>
    /// <returns>The timestamp, or zero when nobody takes the pool trace.</returns>
    [NonEvent]
    public long GetPoolTimestamp()
        => IsEnabled(EventLevel.Verbose, Keywords.Pool) ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Counts and writes a connection that completed its dial and handshake.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the open started.</param>
    [NonEvent]
    public void ConnectionOpened(DatabaseConnection connection, long startTimestamp)
    {
        Interlocked.Increment(ref _currentConnections);
        Interlocked.Increment(ref _totalConnectionsOpened);

        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ConnectionOpened(
                connection.Database,
                connection.DescribeEndPoint(),
                connection.ServerVersion.ToString(),
                GetElapsedMilliseconds(startTimestamp));
        }
    }

    /// <summary>
    /// Counts and writes a connection whose dial or handshake failed.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="exception">The failure the open throws.</param>
    /// <param name="startTimestamp">The timestamp <see cref="GetTimestamp"/> returned when the open started.</param>
    [NonEvent]
    public void ConnectionOpenFailed(DatabaseConnection connection, DatabaseClientException exception, long startTimestamp)
    {
        Interlocked.Increment(ref _totalConnectionFailures);

        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ConnectionOpenFailed(
                connection.Database,
                connection.DescribeEndPoint(),
                exception.Code.ToString(),
                exception.Message,
                GetElapsedMilliseconds(startTimestamp));
        }
    }

    /// <summary>
    /// Counts and writes the first close of a connection that opened.
    /// </summary>
    /// <param name="connection">The connection.</param>
    [NonEvent]
    public void ConnectionClosed(DatabaseConnection connection)
    {
        Interlocked.Decrement(ref _currentConnections);

        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ConnectionClosed(connection.Database, connection.DescribeEndPoint());
        }
    }

    /// <summary>
    /// Writes an open connection that a protocol or transport failure broke.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="exception">The failure that broke it.</param>
    [NonEvent]
    public void ConnectionBroken(DatabaseConnection connection, DatabaseClientException exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ConnectionBroken(connection.Database, exception.Code.ToString(), exception.Message);
        }
    }

    /// <summary>
    /// Counts a connection the pool handed out.
    /// </summary>
    [NonEvent]
    public void RentalStarted()
        => Interlocked.Increment(ref _currentRentedConnections);

    /// <summary>
    /// Counts a rental returned to its pool.
    /// </summary>
    [NonEvent]
    public void RentalEnded()
        => Interlocked.Decrement(ref _currentRentedConnections);

    /// <summary>
    /// Writes a rental.
    /// </summary>
    /// <param name="connection">The connection the pool handed out.</param>
    /// <param name="reused">True when the pool handed out an idle connection; false when it opened a new one.</param>
    /// <param name="waitStartTimestamp">The timestamp <see cref="GetPoolTimestamp"/> returned before the wait for a pool slot.</param>
    /// <param name="waitEndTimestamp">The timestamp taken when the slot was acquired; zero when the first was.</param>
    [NonEvent]
    public void ConnectionRented(DatabaseConnection connection, bool reused, long waitStartTimestamp, long waitEndTimestamp)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Pool))
        {
            double waited = waitStartTimestamp == 0 || waitEndTimestamp == 0
                ? 0
                : Stopwatch.GetElapsedTime(waitStartTimestamp, waitEndTimestamp).TotalMilliseconds;
            ConnectionRented(connection.Database, reused, waited);
        }
    }

    /// <summary>
    /// Writes a returned rental.
    /// </summary>
    /// <param name="connection">The returned connection.</param>
    /// <param name="pooled">True when the connection went back to the idle pool; false when it closed.</param>
    [NonEvent]
    public void ConnectionReturned(DatabaseConnection connection, bool pooled)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Pool))
        {
            ConnectionReturned(connection.Database, pooled);
        }
    }

    /// <summary>
    /// Writes an exchange that failed with a coded error: every coded failure an exchange raises,
    /// whether or not it left the response incomplete. Written by its code and exception type only
    /// (the area's failure rule): a server's statement-level message can quote a key, a blob name or
    /// a fragment of the statement. A connection the failure broke keeps its message through
    /// <c>ConnectionBroken</c>.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="exception">The coded failure.</param>
    [NonEvent]
    public void ExchangeFailed(DatabaseConnection connection, DatabaseClientException exception)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ExchangeFailed(connection.Database, exception.Code.ToString(), exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    /// <summary>
    /// Writes a download whose broken rental could not be returned after the download failed.
    /// </summary>
    /// <param name="connection">The download's connection.</param>
    /// <param name="exception">The failure of the return.</param>
    [NonEvent]
    public void DownloadReleaseFailed(DatabaseConnection connection, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            DownloadReleaseFailed(connection.Database, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Database client connection to '{0}' at {1} opened: protocol {2}, in {3} ms.")]
    private void ConnectionOpened(string database, string endPoint, string serverVersion, double durationMilliseconds)
        => WriteEvent(1, database, endPoint, serverVersion, durationMilliseconds);

    [Event(2, Level = EventLevel.Error, Message = "Database client connection to '{0}' at {1} failed to open ({2}): {3}. After {4} ms.")]
    private void ConnectionOpenFailed(string database, string endPoint, string code, string exceptionMessage, double durationMilliseconds)
        => WriteEvent(2, database, endPoint, code, exceptionMessage, durationMilliseconds);

    [Event(3, Level = EventLevel.Informational, Message = "Database client connection to '{0}' at {1} closed.")]
    private void ConnectionClosed(string database, string endPoint)
        => WriteEvent(3, database, endPoint);

    [Event(4, Level = EventLevel.Warning, Message = "Database client connection to '{0}' broke ({1}): {2}. It closes when its rental is returned.")]
    private void ConnectionBroken(string database, string code, string exceptionMessage)
        => WriteEvent(4, database, code, exceptionMessage);

    [Event(5, Level = EventLevel.Verbose, Keywords = Keywords.Pool, Message = "Rented a connection to '{0}' (reused: {1}) after waiting {2} ms for a pool slot.")]
    private void ConnectionRented(string database, bool reused, double waitedMilliseconds)
        => WriteEvent(5, database, reused, waitedMilliseconds);

    [Event(6, Level = EventLevel.Verbose, Keywords = Keywords.Pool, Message = "Returned a connection to '{0}' (pooled: {1}).")]
    private void ConnectionReturned(string database, bool pooled)
        => WriteEvent(6, database, pooled);

    [Event(7, Level = EventLevel.Verbose, Message = "An exchange on the connection to '{0}' failed: code '{1}', exception '{2}'.")]
    private void ExchangeFailed(string database, string code, string exceptionType)
        => WriteEvent(7, database, code, exceptionType);

    [Event(8, Level = EventLevel.Warning, Message = "A failed download on the connection to '{0}' could not return its rental: {1}: {2}")]
    private void DownloadReleaseFailed(string database, string exceptionType, string exceptionMessage)
        => WriteEvent(8, database, exceptionType, exceptionMessage);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // Created on the first enable command and kept for the source's lifetime. The backing fields
        // are maintained whether or not anyone listens, so a tool that attaches late reads exact values.
        _currentConnectionsCounter ??= new PollingCounter("current-connections", this, () => Volatile.Read(ref _currentConnections))
        {
            DisplayName = "Current Connections",
        };
        _currentRentedConnectionsCounter ??= new PollingCounter("current-rented-connections", this, () => Volatile.Read(ref _currentRentedConnections))
        {
            DisplayName = "Current Rented Connections",
        };
        _connectionsOpenedRateCounter ??= new IncrementingPollingCounter("connections-opened-per-second", this, () => Volatile.Read(ref _totalConnectionsOpened))
        {
            DisplayName = "Connections Opened Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _connectionFailuresCounter ??= new PollingCounter("total-connection-failures", this, () => Volatile.Read(ref _totalConnectionFailures))
        {
            DisplayName = "Total Connection Failures",
        };
    }

    private static double GetElapsedMilliseconds(long startTimestamp)
        => startTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
