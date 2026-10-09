using System;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.Security.Internal;

/// <summary>
/// The Database security child root's diagnostics: the verdict of every authentication attempt the
/// model servers make through <see cref="DatabaseAuthenticator.AuthenticateAsync"/>, and the attempts
/// whose authenticator failed.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Security</c>; applications forward it
/// into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>.
/// </para>
/// <para>
/// The public, non-virtual <see cref="DatabaseAuthenticator.AuthenticateAsync"/> writes every event, so
/// one source covers the built-in authenticator and every application's. A verdict is Verbose: the
/// server's own <c>HandshakeRefused</c> warning is the operator-facing record of a rejection. An
/// authenticator that throws is an infrastructure failure and an Error. Each event names the
/// authenticator by its type name, the database and the claimed principal; the evidence is never
/// written. The database and principal are what an unauthenticated peer sent in its startup frame,
/// which bounds neither, so each is cut to <see cref="MaxNameLength"/> characters, marked with an
/// ellipsis, as the model servers cut the same values in their <c>HandshakeRefused</c>. No counters.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Security")]
internal sealed class DatabaseSecurityEventSource : EventSource
{
    /// <summary>
    /// The longest database or principal name an event writes: what an unauthenticated peer sends is
    /// not bounded by the protocol below a frame's size, and the model servers bound the same names
    /// at the same length.
    /// </summary>
    internal const int MaxNameLength = 256;

    public static readonly DatabaseSecurityEventSource Log = new();

    private DatabaseSecurityEventSource()
    {
    }

    /// <summary>
    /// Gets a value indicating whether any of the source's events may be written, so
    /// <see cref="DatabaseAuthenticator.AuthenticateAsync"/> observes the verdict.
    /// </summary>
    /// <returns>True when a listener takes the source at <see cref="EventLevel.Error"/> or more verbose.</returns>
    [NonEvent]
    public bool IsVerdictTraceEnabled()
        => IsEnabled(EventLevel.Error, EventKeywords.None);

    /// <summary>
    /// Writes the verdict of an authentication attempt.
    /// </summary>
    /// <param name="authenticator">The authenticator that decided.</param>
    /// <param name="database">The database the session wants to bind to.</param>
    /// <param name="principal">The principal the client claims.</param>
    /// <param name="authenticated">The verdict.</param>
    [NonEvent]
    public void AuthenticationCompleted(DatabaseAuthenticator authenticator, string database, string principal, bool authenticated)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            if (authenticated)
            {
                AuthenticationSucceeded(authenticator.GetType().Name, Bound(database, MaxNameLength), Bound(principal, MaxNameLength));
            }
            else
            {
                AuthenticationRejected(authenticator.GetType().Name, Bound(database, MaxNameLength), Bound(principal, MaxNameLength));
            }
        }
    }

    /// <summary>
    /// Writes an authentication attempt whose authenticator threw.
    /// </summary>
    /// <param name="authenticator">The authenticator that failed.</param>
    /// <param name="database">The database the session wants to bind to.</param>
    /// <param name="principal">The principal the client claims.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void AuthenticationFailed(DatabaseAuthenticator authenticator, string database, string principal, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            AuthenticationFailed(
                authenticator.GetType().Name,
                Bound(database, MaxNameLength),
                Bound(principal, MaxNameLength),
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message);
        }
    }

    /// <summary>
    /// Records what a traced member's core threw and returns false, so the exception filter that
    /// calls it catches nothing. The member writes the failure from the <c>finally</c> of the same
    /// <c>try</c>, which runs once the core's own <c>finally</c> blocks released what they held; a
    /// filter runs before them, while a core that threw inside a lock still holds it.
    /// </summary>
    /// <param name="exception">What the core threw.</param>
    /// <param name="captured">Receives <paramref name="exception"/>.</param>
    /// <returns>False, always.</returns>
    public static bool CaptureFailure(Exception exception, out Exception captured)
    {
        captured = exception;
        return false;
    }

    /// <summary>
    /// Cuts a name to <paramref name="maxLength"/> characters and marks the cut with an ellipsis,
    /// without splitting a surrogate pair.
    /// </summary>
    private static string Bound(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return string.Concat(value.AsSpan(0, length), "...");
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Authenticator {0} authenticated principal '{2}' for database '{1}'.")]
    private void AuthenticationSucceeded(string authenticator, string database, string principal)
        => WriteEvent(1, authenticator, database, principal);

    [Event(2, Level = EventLevel.Verbose, Message = "Authenticator {0} rejected principal '{2}' for database '{1}'.")]
    private void AuthenticationRejected(string authenticator, string database, string principal)
        => WriteEvent(2, authenticator, database, principal);

    [Event(3, Level = EventLevel.Error, Message = "Authenticator {0} failed while authenticating principal '{2}' for database '{1}': {3}: {4}")]
    private void AuthenticationFailed(string authenticator, string database, string principal, string exceptionType, string exceptionMessage)
        => WriteEvent(3, authenticator, database, principal, exceptionType, exceptionMessage);
}
