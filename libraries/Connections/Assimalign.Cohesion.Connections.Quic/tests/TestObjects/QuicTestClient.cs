using System;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

/// <summary>
/// Dials loopback QUIC listeners that speak the test ALPN protocol (<c>cohesion-test</c>), with or without a
/// client certificate.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class QuicTestClient
{
    /// <summary>
    /// Creates a client factory that trusts any server certificate and presents
    /// <paramref name="clientCertificate"/> when it is not <see langword="null"/>.
    /// </summary>
    public static QuicConnectionFactory CreateFactory(X509Certificate2? clientCertificate)
    {
        return QuicConnectionFactory.Create(options =>
        {
            options.ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [new SslApplicationProtocol("cohesion-test")],
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            };

            if (clientCertificate is not null)
            {
                options.ClientAuthenticationOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
                options.ClientAuthenticationOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate;
            }
        });
    }

    /// <summary>
    /// Connects without a client certificate to a listener that requires one, and returns once the client
    /// has learned of the refusal, by which point the listener has failed the handshake.
    /// </summary>
    public static async Task ConnectRefusedAsync(EndPoint endPoint, CancellationToken cancellationToken)
    {
        MultiplexedConnection refused;

        try
        {
            refused = await CreateFactory(clientCertificate: null).ConnectAsync(endPoint, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Refused during the handshake.
            return;
        }

        // Under TLS 1.3 the client can finish its side of the handshake before the server checks its
        // certificate; the refusal then arrives as the connection's close, which ends a wait for a stream.
        await using (refused)
        {
            try
            {
                await using Connection stream = await refused.AcceptStreamAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return;
            }

            throw new InvalidOperationException("The listener opened a stream on a connection it should have refused.");
        }
    }
}
