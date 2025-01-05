using System.Net;
using System.Net.Security;
using System.Text;
using System.Buffers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Net.Transports.Tests;

public class TcpTransportTests
{
    [Fact]
    public void RequestResponseTest()
    {
        int i = 0;
        var server = GetServer(message =>
        {
            Assert.Equal("Client -> Server: Hello", message);
            i++;
        });
        var client = GetClient(message =>
        {
            Assert.Equal("Server -> Client: Hello", message);
            i++;
        });

        var timestamp = DateTime.Now;

        server.Start();
        client.Start();

        while (i != 2)
        {
            if (DateTime.Now > timestamp.AddSeconds(10))
            {
                Assert.Fail("The process ran too long and was unable to complete.");
            }
        }
    }

    private Thread GetClient(Action<string> callback)
    {
        return new Thread(async () =>
        {
            using var transport = TcpClientTransport.Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8081);
                options.AddMiddleware(builder =>
                {
                    builder.UseNext(async (context, next) =>
                    {
                        var pipe = context.Connection.Pipe;
                        var stream = pipe.GetStream();
                        var sslStream = new SslStream(stream, true);

                        await sslStream.AuthenticateAsClientAsync("localhost");

                        context.SetPipe(new TransportConnectionPipe(sslStream));

                        await next.Invoke(context);
                    });
                });
            });

            var connection = await transport.ConnectAsync();

            var message = Encoding.UTF8.GetBytes("Client -> Server: Hello");
            var memory = new ReadOnlyMemory<byte>(message);

            await connection.Pipe.WriteAsync(memory);

            var result = await connection.Pipe.ReadAsync();
            var buffer = result.Buffer.ToArray();
            var data = Encoding.UTF8.GetString(buffer);

            callback.Invoke(data);
        });
    }
    private Thread GetServer(Action<string> callback)
    {
        return new Thread(async () =>
        {
            using var transport = TcpServerTransport.Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8081);
                options.AddMiddleware(builder =>
                {
                    builder.UseNext(async (context, next) =>
                    {
                        var pipe = context.Connection.Pipe;
                        var stream = pipe.GetStream();
                        var sslStream = new SslStream(stream);
                        var certificate = GetSelfSignedCertificate();

                        await sslStream.AuthenticateAsServerAsync(certificate);

                        context.SetPipe(new TransportConnectionPipe(sslStream));

                        await next.Invoke(context);
                    });
                });
            });

            var connection = await transport.AcceptOrListenAsync();
            var result = await connection.Pipe.ReadAsync();
            var buffer = result.Buffer.ToArray();
            var data = Encoding.UTF8.GetString(buffer);

            callback.Invoke(data);

            var message = Encoding.UTF8.GetBytes("Server -> Client: Hello");
            var memory = new ReadOnlyMemory<byte>(message);

            await connection.Pipe.WriteAsync(memory);

            // Need to wait for the client to receive data before disposing of the underlying transport
            await Task.Delay(3000);
        });
    }
    private X509Certificate2 GetSelfSignedCertificate()
    {
        X509Certificate2? certificate;

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);

        if (!store.IsOpen)
        {
            store.Open(OpenFlags.ReadWrite);
        }

        certificate = store.Certificates.FirstOrDefault(p => p.Issuer == "CN=localhost" && p.FriendlyName == "");

        if (certificate is not null)
        {
            return certificate;
        }
        var timestamp = DateTimeOffset.UtcNow;
        var sanBuilder = new SubjectAlternativeNameBuilder();

        sanBuilder.AddDnsName("localhost");

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest("CN=localhost", ec, HashAlgorithmName.SHA256);
        // Adds purpose
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1") // serverAuth
        }, false));


        // Adds usage
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        // Adds subject alternate names
        certificateRequest.CertificateExtensions.Add(sanBuilder.Build());
        // Sign
        using var crt = certificateRequest.CreateSelfSigned(timestamp, timestamp.AddDays(365)); // 14 days is the max duration of a certificate for this

        certificate = new X509Certificate2(crt.Export(X509ContentType.Pfx));

        // We need to add the certificate to the store so error is not thrown due to invalid cert chain
        store.Add(certificate);

        return certificate;
    }
}