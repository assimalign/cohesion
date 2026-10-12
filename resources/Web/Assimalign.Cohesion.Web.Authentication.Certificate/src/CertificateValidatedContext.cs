using System;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication.Certificate;

/// <summary>Application validation of a certificate already accepted by the connection's TLS policy.</summary>
public sealed class CertificateValidatedContext
{
    internal CertificateValidatedContext(
        IHttpContext httpContext,
        AuthenticationScheme scheme,
        X509Certificate2 clientCertificate,
        ClaimsPrincipal principal)
    {
        HttpContext = httpContext;
        Scheme = scheme;
        ClientCertificate = clientCertificate;
        Principal = principal;
    }

    /// <summary>Gets the current HTTP exchange.</summary>
    public IHttpContext HttpContext { get; }

    /// <summary>Gets the authentication scheme evaluating the certificate.</summary>
    public AuthenticationScheme Scheme { get; }

    /// <summary>Gets the validated client certificate borrowed from the connection.</summary>
    /// <remarks>Do not dispose this instance. Copy it if it must outlive the exchange.</remarks>
    public X509Certificate2 ClientCertificate { get; }

    /// <summary>Gets or sets the mapped principal; null or an unauthenticated identity fails authentication.</summary>
    public ClaimsPrincipal? Principal { get; set; }

    /// <summary>Gets the first application rejection reason, or null when the callback has not rejected it.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>Rejects this credential, even if the callback subsequently replaces the principal.</summary>
    /// <param name="failureMessage">The rejection reason, retained in the authentication result, never sent in a challenge.</param>
    /// <exception cref="ArgumentException">The reason is null or whitespace.</exception>
    public void Fail(string failureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        FailureMessage ??= failureMessage;
    }
}
