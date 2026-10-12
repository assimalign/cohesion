using System;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Internal;

internal sealed class CertificateAuthenticationHandler : IAuthenticationHandler
{
    private readonly Func<CertificateValidatedContext, CancellationToken, ValueTask>? _onCertificateValidated;
    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;
    private Task<AuthenticateResult>? _authentication;

    public CertificateAuthenticationHandler(Func<CertificateValidatedContext, CancellationToken, ValueTask>? onCertificateValidated)
    {
        _onCertificateValidated = onCertificateValidated;
    }

    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _scheme = scheme;
        _context = context;
        _authentication = null;
        return Task.CompletedTask;
    }

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
            return AuthenticateResult.Fail(failure);
        }

        if (validation.Principal?.Identity?.IsAuthenticated != true)
        {
            return AuthenticateResult.Fail("The certificate mapping did not produce an authenticated principal.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(
            validation.Principal, new AuthenticationProperties(), _scheme.Name));
    }

    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This application-specific challenge cannot renegotiate TLS or acquire a certificate.
        _context.Response.Headers.AppendValue(HttpHeaderKey.WWWAuthenticate, CertificateAuthenticationDefaults.AuthenticationScheme);
        _context.Response.StatusCode = HttpStatusCode.Unauthorized;
        return Task.CompletedTask;
    }

    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.Response.StatusCode = HttpStatusCode.Forbidden;
        return Task.CompletedTask;
    }
}
