using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authentication.Certificate;

/// <summary>Configures identity mapping and application validation for a TLS client certificate.</summary>
/// <remarks>
/// TLS trust is configured on the listener, through its client-certificate mode and validator.
/// These options cannot request a certificate or relax that policy. Registration snapshots the options.
/// </remarks>
public sealed class CertificateAuthenticationOptions
{
    /// <summary>Gets or sets the scheme's human-readable display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the asynchronous callback for additional validation and principal mapping.</summary>
    /// <remarks>
    /// Runs once per request and scheme, only when TLS supplied a certificate. The context initially
    /// carries the certificate's subject, issuer, thumbprint and serial claims. Replace or augment its
    /// principal, or call <see cref="CertificateValidatedContext.Fail"/> to reject it. A null or
    /// unauthenticated replacement also fails. Cancellation and callback exceptions propagate.
    /// The callback may run concurrently for different requests and must be thread-safe.
    /// </remarks>
    public Func<CertificateValidatedContext, CancellationToken, ValueTask>? OnCertificateValidated { get; set; }
}
