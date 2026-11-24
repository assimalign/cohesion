using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// App registrations the tests share: an API, a web application that signs users in, and a
/// daemon that authenticates as itself. Each takes the name its identifier is derived from.
/// </summary>
internal static class TestClients
{
    public static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An API: one audience, the scope <c>orders.read</c>, and the role <c>orders.process</c>
    /// open to applications.
    /// </summary>
    public static IdentityClient Api(string name = "orders-api", string audience = "api://orders")
    {
        return IdentityClient.Create(new()
        {
            Id = IdentityClientId.FromName(name),
            DisplayName = "Orders API",
            Audiences = { audience },
            Scopes = { IdentityClientScope.Create("orders.read", "Read orders") },
            Roles = { IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Applications) },
        });
    }

    /// <summary>
    /// A public web application that signs users in and reads orders on their behalf.
    /// </summary>
    public static IdentityClient Web(string name = "orders-web", string api = "orders-api")
    {
        return IdentityClient.Create(new()
        {
            Id = IdentityClientId.FromName(name),
            Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.RefreshToken,
            Redirects = { IdentityClientRedirect.Web("https://orders.example/signin") },
            Permissions = { IdentityClientPermission.Delegated(IdentityClientId.FromName(api), "orders.read") },
        });
    }

    /// <summary>
    /// A confidential daemon that processes orders as itself.
    /// </summary>
    public static IdentityClient Daemon(string name = "orders-daemon", string api = "orders-api")
    {
        return IdentityClient.Create(new()
        {
            Id = IdentityClientId.FromName(name),
            Grants = IdentityClientGrants.ClientCredentials,
            Credentials = { IdentityClientSecretCredential.Generate("primary", out _) },
            Permissions = { IdentityClientPermission.Application(IdentityClientId.FromName(api), "orders.process") },
        });
    }

    /// <summary>
    /// A self-signed certificate with no private key attached to the returned bytes.
    /// </summary>
    public static X509Certificate2 Certificate(DateTimeOffset notBefore, DateTimeOffset notAfter, string subject = "CN=orders-daemon")
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        using X509Certificate2 signed = request.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadCertificate(signed.RawData);
    }
}
