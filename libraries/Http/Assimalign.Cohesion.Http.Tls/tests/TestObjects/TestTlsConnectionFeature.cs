using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Http.Tls.Tests.TestObjects;

/// <summary>
/// An application-supplied <see cref="IHttpTlsConnectionFeature"/>, as a middleware installs one to
/// override what the connection reports. It registers under the built feature's name unless the test
/// names another slot.
/// </summary>
internal sealed class TestTlsConnectionFeature : IHttpTlsConnectionFeature
{
    /// <summary>The name the feature <c>context.TlsConnection</c> builds registers under.</summary>
    public const string BuiltFeatureName = "Assimalign.Cohesion.Http.TlsConnection";

    public TestTlsConnectionFeature(string name = BuiltFeatureName)
    {
        Name = name;
    }

    public string Name { get; }

    public X509Certificate2? ClientCertificate => null;

    public SslProtocols Protocol => SslProtocols.Tls12;

    public TlsCipherSuite CipherSuite => TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384;

    public SslApplicationProtocol ApplicationProtocol => SslApplicationProtocol.Http11;
}
