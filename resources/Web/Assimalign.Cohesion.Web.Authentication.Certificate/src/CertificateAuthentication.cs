using System;

using Assimalign.Cohesion.Web.Authentication.Certificate.Internal;

namespace Assimalign.Cohesion.Web.Authentication.Certificate;

/// <summary>Creates client-certificate handlers through the shared authentication contract.</summary>
public static class CertificateAuthentication
{
    /// <summary>Creates a handler with a snapshot of the supplied options.</summary>
    /// <param name="options">Application mapping and validation options.</param>
    /// <returns>A fresh per-request authentication handler.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    public static IAuthenticationHandler CreateHandler(CertificateAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CertificateAuthenticationHandler(options.OnCertificateValidated);
    }
}
