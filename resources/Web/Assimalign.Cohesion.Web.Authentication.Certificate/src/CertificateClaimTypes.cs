namespace Assimalign.Cohesion.Web.Authentication.Certificate;

/// <summary>Certificate claim types without an equivalent in <c>System.Security.Claims.ClaimTypes</c>.</summary>
public static class CertificateClaimTypes
{
    /// <summary>The certificate issuer's distinguished name, as reported by <c>X509Certificate2.Issuer</c>.</summary>
    public const string Issuer = "urn:cohesion:certificate:issuer";
}
