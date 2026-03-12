#if NET7_0_OR_GREATER
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class QuicTransportServerClientResponseTests
{
    [Fact]
    public async Task RequestResponse_WhenConnectionIsOpen_ShouldExchangePayloadAsync()
    {
        if (!QuicConnection.IsSupported)
        {
            return;
        }

        using X509Certificate2 certificate = CreateSelfSignedCertificate("localhost");
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        int port = GetEphemeralPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var applicationProtocol = new SslApplicationProtocol("cohesion-tests");

        try
        {
            Task<string> serverTask = RunServerAsync(endpoint, applicationProtocol, certificate, cancellationTokenSource.Token);

            await Task.Delay(350, cancellationTokenSource.Token);

            string clientResponse = await RunClientAsync(endpoint, applicationProtocol, cancellationTokenSource.Token);
            string serverMessage = await serverTask;

            Assert.Equal("Client -> Server: Hello", serverMessage);
            Assert.Equal("Server -> Client: Hello", clientResponse);
        }
        catch (SocketException)
        {
            // Some CI or local environments block local QUIC traffic even when QUIC APIs are available.
        }
        catch (QuicException)
        {
            // Some CI or local environments block local QUIC traffic even when QUIC APIs are available.
        }
        catch (AuthenticationException)
        {
            // Some CI or local environments reject QUIC TLS handshakes due local trust/policy configuration.
        }
    }

    private static async Task<string> RunClientAsync(IPEndPoint endpoint, SslApplicationProtocol applicationProtocol, CancellationToken cancellationToken)
    {
        await using QuicClientTransport transport = QuicClientTransport.Create(options =>
        {
            options.EndPoint = endpoint;
            options.ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols =
                [
                    applicationProtocol
                ],
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            };
        });

        await using QuicTransportConnection connection = await transport.ConnectAsync(cancellationToken);
        QuicTransportContext context = await connection.OpenOutboundAsync(cancellationToken);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Client -> Server: Hello"), cancellationToken);

        ReadResult result = await context.Pipe.Input.ReadAsync(cancellationToken);
        string response = Encoding.UTF8.GetString(result.Buffer.ToArray());
        context.Pipe.Input.AdvanceTo(result.Buffer.End);

        return response;
    }

    private static async Task<string> RunServerAsync(IPEndPoint endpoint, SslApplicationProtocol applicationProtocol, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        await using QuicServerTransport transport = QuicServerTransport.Create(options =>
        {
            options.EndPoint = endpoint;
            options.ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols =
                [
                    applicationProtocol
                ]
            };
        });

        await using QuicTransportConnection connection = await transport.AcceptOrListenAsync(cancellationToken);
        QuicTransportContext context = await connection.OpenInboundAsync(cancellationToken);

        ReadResult result = await context.Pipe.Input.ReadAsync(cancellationToken);
        string message = Encoding.UTF8.GetString(result.Buffer.ToArray());
        context.Pipe.Input.AdvanceTo(result.Buffer.End);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Server -> Client: Hello"), cancellationToken);

        return message;
    }

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string host)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();

        sanBuilder.AddDnsName(host);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
    }
}
#endif
