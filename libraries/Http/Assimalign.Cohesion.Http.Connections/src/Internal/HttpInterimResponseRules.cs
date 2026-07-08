using System;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Status-code rules shared by all three protocol engines' response paths for interim (<c>1xx</c>)
/// responses (RFC 9110 §15.2). Interim responses are emitted through the transport's
/// <see cref="IHttpExchangeControl"/> interim writes (wrapped by the <c>Http.InterimResponses</c>
/// feature package); the final response must never carry a <c>1xx</c> status.
/// </summary>
internal static class HttpInterimResponseRules
{
    /// <summary>
    /// The lowest <c>1xx</c> status code (inclusive).
    /// </summary>
    private const int FirstInformationalStatus = 100;

    /// <summary>
    /// The highest <c>1xx</c> status code (inclusive).
    /// </summary>
    private const int LastInformationalStatus = 199;

    /// <summary>
    /// <c>101 Switching Protocols</c> — a connection transition owned by the protocol-upgrade
    /// package, not an interim response the feature emits.
    /// </summary>
    private const int SwitchingProtocolsStatus = 101;

    /// <summary>
    /// Returns whether <paramref name="statusCode"/> is an informational (<c>1xx</c>) status.
    /// </summary>
    /// <param name="statusCode">The status code to classify.</param>
    /// <returns><see langword="true"/> when the code is in the 100–199 range.</returns>
    public static bool IsInformational(HttpStatusCode statusCode)
        => (int)statusCode is >= FirstInformationalStatus and <= LastInformationalStatus;

    /// <summary>
    /// Validates a status code supplied to <see cref="IHttpExchangeControl.WriteInterimResponseAsync"/>:
    /// it MUST be <c>1xx</c> and MUST NOT be <c>101 Switching Protocols</c>.
    /// </summary>
    /// <param name="statusCode">The interim status code to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="statusCode"/> is outside the <c>1xx</c> range, or is <c>101</c>.
    /// </exception>
    public static void ValidateInterimStatusCode(HttpStatusCode statusCode)
    {
        if (!IsInformational(statusCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode.Value,
                "An interim response requires a 1xx status code (RFC 9110 §15.2).");
        }

        if ((int)statusCode == SwitchingProtocolsStatus)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode.Value,
                "101 Switching Protocols is a connection transition owned by Assimalign.Cohesion.Http.ProtocolUpgrade, not an interim response.");
        }
    }

    /// <summary>
    /// Guards a transport's final-response write: a <c>1xx</c> status is never a valid <em>final</em>
    /// status (RFC 9110 §15.2 — an interim response is not the final response). Interim responses go
    /// through <see cref="IHttpExchangeControl"/>; the sole <c>1xx</c> that ends an exchange is
    /// <c>101 Switching Protocols</c>, which the HTTP/1.1 protocol-upgrade path finalizes out-of-band
    /// (its send is suppressed) and so never reaches this guard.
    /// </summary>
    /// <param name="statusCode">The status code about to be written as the final response.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="statusCode"/> is a <c>1xx</c> informational status.
    /// </exception>
    public static void EnsureFinalStatusCode(HttpStatusCode statusCode)
    {
        if (IsInformational(statusCode))
        {
            throw new InvalidOperationException(
                $"A 1xx status code ({statusCode}) cannot be used as the final response status; emit interim responses through IHttpInterimResponseFeature instead.");
        }
    }
}
