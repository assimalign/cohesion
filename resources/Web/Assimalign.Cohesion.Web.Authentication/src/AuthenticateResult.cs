using System;
using System.Security.Claims;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The immutable outcome of an <see cref="IAuthenticationHandler.AuthenticateAsync"/> call. A
/// result is one of exactly three states: <em>success</em> (a ticket was produced),
/// <em>no result</em> (the scheme found no credential to evaluate), or <em>failure</em> (a
/// credential was present but rejected).
/// </summary>
/// <remarks>
/// Failure is a value, not an exception: a rejected credential is a normal, expected outcome
/// that carries its reason. This mirrors the IdentityModel token/authentication result contracts
/// (a computed <c>Succeeded</c> flag so a "succeeded with a failure" state is unconstructible),
/// while keeping the Web layer in <see cref="ClaimsPrincipal"/> terms via
/// <see cref="AuthenticationTicket"/>.
/// </remarks>
public sealed class AuthenticateResult
{
    private AuthenticateResult(AuthenticationTicket? ticket, Exception? failure, bool none)
    {
        Ticket = ticket;
        Failure = failure;
        None = none;
    }

    /// <summary>
    /// Gets a value indicating whether authentication succeeded. When <see langword="true"/>,
    /// <see cref="Ticket"/> and <see cref="Principal"/> are non-null.
    /// </summary>
    public bool Succeeded => Ticket is not null;

    /// <summary>
    /// Gets a value indicating whether the scheme produced no result because there was no
    /// credential to evaluate (for example no cookie or no <c>Authorization</c> header).
    /// </summary>
    public bool None { get; }

    /// <summary>
    /// Gets the produced ticket, or <see langword="null"/> when the result is not a success.
    /// </summary>
    public AuthenticationTicket? Ticket { get; }

    /// <summary>
    /// Gets the authenticated principal, or <see langword="null"/> when the result is not a
    /// success.
    /// </summary>
    public ClaimsPrincipal? Principal => Ticket?.Principal;

    /// <summary>
    /// Gets the properties associated with the result: the ticket's properties on success, the
    /// carried failure properties otherwise, or <see langword="null"/>.
    /// </summary>
    public AuthenticationProperties? Properties { get; private init; }

    /// <summary>
    /// Gets the failure that caused authentication to be rejected, or <see langword="null"/>
    /// when the result is not a failure.
    /// </summary>
    public Exception? Failure { get; }

    /// <summary>
    /// Creates a successful result carrying <paramref name="ticket"/>.
    /// </summary>
    /// <param name="ticket">The authenticated ticket.</param>
    /// <returns>A successful result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ticket"/> is <see langword="null"/>.</exception>
    public static AuthenticateResult Success(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return new AuthenticateResult(ticket, failure: null, none: false) { Properties = ticket.Properties };
    }

    /// <summary>
    /// Creates a "no result" outcome: the scheme found no credential to evaluate.
    /// </summary>
    /// <returns>A no-result outcome.</returns>
    public static AuthenticateResult NoResult()
        => new(ticket: null, failure: null, none: true);

    /// <summary>
    /// Creates a failure result from an exception.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="properties">Optional properties to carry with the failure.</param>
    /// <returns>A failure result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    public static AuthenticateResult Fail(Exception failure, AuthenticationProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new AuthenticateResult(ticket: null, failure, none: false) { Properties = properties };
    }

    /// <summary>
    /// Creates a failure result from a message.
    /// </summary>
    /// <param name="failureMessage">The failure reason.</param>
    /// <param name="properties">Optional properties to carry with the failure.</param>
    /// <returns>A failure result.</returns>
    /// <exception cref="ArgumentException"><paramref name="failureMessage"/> is <see langword="null"/> or whitespace.</exception>
    public static AuthenticateResult Fail(string failureMessage, AuthenticationProperties? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        return Fail(new AuthenticationFailureException(failureMessage), properties);
    }
}
