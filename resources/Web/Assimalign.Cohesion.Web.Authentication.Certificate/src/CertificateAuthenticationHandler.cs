using System;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Certificate.Internal;

namespace Assimalign.Cohesion.Web.Authentication.Certificate;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>Authenticates requests with a TLS client certificate and optional application validation.</summary>
[global::System.Diagnostics.DebuggerDisplay("CertificateAuthenticationHandler")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class CertificateAuthenticationHandler : IAuthenticationHandler
{
    private static readonly string _certificatePrincipalNotAuthenticatedMessage = ErrorMessages.CertificatePrincipalNotAuthenticated;

    private readonly Func<CertificateValidatedContext, CancellationToken, ValueTask>? _onCertificateValidated;
    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;
    private Task<AuthenticateResult>? _authentication;
    private int _rejectionReported;

    private CertificateAuthenticationHandler(Func<CertificateValidatedContext, CancellationToken, ValueTask>? onCertificateValidated)
    {
        _onCertificateValidated = onCertificateValidated;
    }

    /// <summary>Creates a handler with a snapshot of the supplied options.</summary>
    /// <param name="options">Application mapping and validation options.</param>
    /// <returns>A fresh per-request authentication handler.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    public static CertificateAuthenticationHandler Create(CertificateAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CertificateAuthenticationHandler(options.OnCertificateValidated);
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _scheme = scheme;
        _context = context;
        _authentication = null;
        _rejectionReported = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _authentication ??= AuthenticateCoreAsync(cancellationToken);
    }

    private async Task<AuthenticateResult> AuthenticateCoreAsync(CancellationToken cancellationToken)
    {
        // Trust belongs to the endpoint's handshake policy. Reading the accessor also materializes
        // the lazy Http.Tls feature; a bare Features.Get would miss real TLS connections.
        X509Certificate2? certificate = _context.TlsConnection?.ClientCertificate;
        if (certificate is null)
        {
            return AuthenticateResult.NoResult();
        }

        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.X500DistinguishedName, certificate.Subject),
                new Claim(CertificateClaimTypes.Issuer, certificate.Issuer),
                new Claim(ClaimTypes.Thumbprint, certificate.Thumbprint),
                new Claim(ClaimTypes.SerialNumber, certificate.SerialNumber),
            ],
            _scheme.Name,
            ClaimTypes.X500DistinguishedName,
            ClaimTypes.Role);

        CertificateValidatedContext validation = new(_context, _scheme, certificate, new ClaimsPrincipal(identity));
        if (_onCertificateValidated is not null)
        {
            await _onCertificateValidated(validation, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (validation.FailureMessage is { } failure)
        {
            ReportRejection("ValidationFailed");
            return AuthenticateResult.Fail(failure);
        }

        if (validation.Principal?.Identity?.IsAuthenticated != true)
        {
            ReportRejection("ValidationFailed");
            return AuthenticateResult.Fail(_certificatePrincipalNotAuthenticatedMessage);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(
            validation.Principal, new AuthenticationProperties(), _scheme.Name));
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context.TlsConnection?.ClientCertificate is null)
        {
            // Absence is NoResult during optional authentication. A challenge is the handler's
            // decision that this request needs a certificate, so only this path counts absence.
            ReportRejection("NoCertificate");
        }

        // This application-specific challenge cannot renegotiate TLS or acquire a certificate.
        _context.Response.Headers.AppendValue(HttpHeaderKey.WWWAuthenticate, CertificateAuthenticationDefaults.AuthenticationScheme);
        _context.Response.StatusCode = HttpStatusCode.Unauthorized;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.Response.StatusCode = HttpStatusCode.Forbidden;
        return Task.CompletedTask;
    }

    private void ReportRejection(string reason)
    {
        // Authentication and a later challenge share one request/scheme rejection, including
        // repeated challenges and an application replacing the TLS feature between those calls.
        if (Interlocked.Exchange(ref _rejectionReported, 1) == 0)
        {
            CertificateAuthenticationEventSource.Log.CertificateRejected(_context, _scheme.Name, reason);
        }
    }

    private sealed class DebugView
    {
        public DebugView(CertificateAuthenticationHandler value)
        {
        }
    }
}
