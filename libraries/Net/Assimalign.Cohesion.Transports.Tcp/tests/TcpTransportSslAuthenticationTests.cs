using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
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

public class TcpTransportSslAuthenticationTests
{
    [Fact]
    public async Task RequestResponse_WhenSslHandshakeCompletes_ShouldExchangeEncryptedPayloadAsync()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using X509Certificate2 certificate = CreateSelfSignedCertificate("localhost");

        int port = GetEphemeralPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        Task<string> serverTask = RunSslServerAsync(endpoint, certificate, cancellationTokenSource.Token);

        await Task.Delay(200, cancellationTokenSource.Token);

        string clientResponse = await RunSslClientAsync(endpoint, cancellationTokenSource.Token);
        string serverMessage = await serverTask;

        Assert.Equal("Client -> Server: Hello", serverMessage);
        Assert.Equal("Server -> Client: Hello", clientResponse);
    }

    private static async Task<string> RunSslServerAsync(IPEndPoint endpoint, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        await using TcpServerTransport transport = TcpServerTransport.Create(options =>
        {
            options.EndPoint = endpoint;
            options.UseSecureConnection(options =>
            {
                options.ServerCertificate = certificate;
                options.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                options.ClientCertificateRequired = false;
            }); 
            //options.Use(async (context, next) =>
            //{
            //    ITransportConnectionPipe pipe = context.Pipe;
            //    Stream stream = pipe.GetStream();

            //    var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);

            //    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            //    {
            //        ServerCertificate = certificate,
            //        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            //        ClientCertificateRequired = false
            //    }, cancellationToken).ConfigureAwait(false);

            //    context.SetPipe(new TransportConnectionPipe(sslStream));

            //    await next(context).ConfigureAwait(false);
            //});
        });

        await using TcpTransportConnection connection = await transport.AcceptOrListenAsync(cancellationToken);
        TcpTransportConnectionContext context = await connection.OpenAsync(cancellationToken);

        ReadResult result = await context.Pipe.Input.ReadAsync(cancellationToken);
        string message = Encoding.UTF8.GetString(result.Buffer.ToArray());
        context.Pipe.Input.AdvanceTo(result.Buffer.End);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Server -> Client: Hello"), cancellationToken);

        await Task.Delay(100, cancellationToken);

        return message;
    }

    private static async Task<string> RunSslClientAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await using TcpClientTransport transport = TcpClientTransport.Create(options =>
        {
            options.EndPoint = endpoint;
            options.UseSecureConnection(options =>
            {
                options.TargetHost = "localhost";
                options.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            });
            //options.Use(async (context, next) =>
            //{
            //    ITransportConnectionPipe pipe = context.Pipe;
            //    Stream stream = pipe.GetStream();

            //    var sslStream = new SslStream(
            //        stream,
            //        leaveInnerStreamOpen: false,
            //        userCertificateValidationCallback: static (_, _, _, _) => true);

            //    await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            //    {
            //        TargetHost = "localhost",
            //        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            //    }, cancellationToken).ConfigureAwait(false);

            //    context.SetPipe(new TransportConnectionPipe(sslStream));

            //    await next(context).ConfigureAwait(false);
            //});
        });

        await using TcpTransportConnection connection = await transport.ConnectAsync(cancellationToken);
        TcpTransportConnectionContext context = await connection.OpenAsync(cancellationToken);

        await context.Pipe.Output.WriteAsync(Encoding.UTF8.GetBytes("Client -> Server: Hello"), cancellationToken);

        ReadResult result = await context.Pipe.Input.ReadAsync(cancellationToken);
        string response = Encoding.UTF8.GetString(result.Buffer.ToArray());
        context.Pipe.Input.AdvanceTo(result.Buffer.End);

        return response;
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
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        // Windows SChannel rejects ephemeral keys for server auth; round-trip through PFX so the
        // private key is loaded in a persistable form acceptable to all platforms.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
