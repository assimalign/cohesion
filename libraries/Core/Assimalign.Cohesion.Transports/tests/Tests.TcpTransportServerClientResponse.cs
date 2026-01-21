using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TcpTransportServerClientResponseTests
{
    const string LocalhostName = "localhost";


    [Fact]
    public void TestRequestResponse()
    {
        using X509Store store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        using RSA rsa = RSA.Create();

        if (!store.IsOpen)
        {
            store.Open(OpenFlags.ReadWrite);
        }

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(LocalhostName);

        var certificateRequest = new CertificateRequest($"CN={LocalhostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        certificateRequest.CertificateExtensions.Add(sanBuilder.Build());
        X509Certificate2 certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            //certificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Pfx));

            certificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx));
            store.Add(certificate);
        }


        // Begin Testing pipeline
        int i = 0;
        var server = GetServer(message =>
        {
            Assert.Equal("Client -> Server: Hello", message);
            i++;
        }, certificate);
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
            if (DateTime.Now > timestamp.AddSeconds(20))
            {
                Assert.Fail("The process ran too long and was unable to complete.");
            }
        }
    }
    private static Thread GetClient(Action<string> callback)
    {
        return new Thread(async () =>
        {
            await using (var transport = TcpClientTransport.Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8081);
                options.Use(async (connection, context, next) =>
                {
                    var pipe = context.Pipe;
                    var stream = pipe.GetStream();
                    var sslStream = new SslStream(stream, true);

                    await sslStream.AuthenticateAsClientAsync(LocalhostName);

                    context.SetPipe(new TransportConnectionPipe(sslStream));

                    await next.Invoke(connection, context);
                });
            }))
            {
                await using (ISingleStreamTransportConnection connection = await transport.ConnectAsync())
                {
                    ITransportConnectionContext context = await connection.OpenAsync();

                    byte[] message = Encoding.UTF8.GetBytes("Client -> Server: Hello");

                    ReadOnlyMemory<byte> memory = new ReadOnlyMemory<byte>(message);

                    FlushResult flush = await context.Pipe.WriteAsync(memory);

                    var result = await context.Pipe.ReadAsync();
                    var buffer = result.Buffer.ToArray();
                    var data = Encoding.UTF8.GetString(buffer);

                    callback.Invoke(data);
                }
            }
        });
    }
    private static Thread GetServer(Action<string> callback, X509Certificate2 certificate)
    {
        return new Thread(async () =>
        {
            await using (var transport = TcpServerTransport.Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8081);
                options.Use(async (connection, context, next) =>
                {
                    var pipe = context.Pipe;
                    var stream = pipe.GetStream();
                    var sslStream = new SslStream(stream);

                    await sslStream.AuthenticateAsServerAsync(certificate);

                    context.SetPipe(new TransportConnectionPipe(sslStream));

                    await next.Invoke(connection, context);
                });
            }))
            {
                await using (var connection = await transport.AcceptOrListenAsync())
                {
                    var context = await connection.OpenAsync();
                    var result = await context.Pipe.ReadAsync();
                    var buffer = result.Buffer.ToArray();
                    var data = Encoding.UTF8.GetString(buffer);

                    callback.Invoke(data);

                    var message = Encoding.UTF8.GetBytes("Server -> Client: Hello");
                    var memory = new ReadOnlyMemory<byte>(message);

                    await context.Pipe.WriteAsync(memory);

                    // Need to wait for the client to receive data before disposing of the underlying transport
                    await Task.Delay(5000);
                }
            }
        });
    }

    private static X509Certificate2 GenerateTestCertificateWithAlgorithm(string algorithmType, string? keyPassword, string certificatePath, string keyPath)
    {
        var distinguishedName = new X500DistinguishedName($"CN=test.{algorithmType}.local");
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName($"test.{algorithmType}.local");

        X509Certificate2 certificate;
        string keyPem;

        switch (algorithmType)
        {
            case "RSA":
                using (var rsa = RSA.Create(2048))
                {
                    var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    certificate = CreateTestCertificate(request, sanBuilder);
                    keyPem = ExportKeyToPem(rsa, keyPassword);
                }
                break;

            case "ECDsa":
                using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    var request = new CertificateRequest(distinguishedName, ecdsa, HashAlgorithmName.SHA256);
                    certificate = CreateTestCertificate(request, sanBuilder);
                    keyPem = ExportKeyToPem(ecdsa, keyPassword);
                }
                break;

            default:
                throw new ArgumentException($"Unknown algorithm type: {algorithmType}");
        }

        if (certificatePath.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
        {
            // Export the certificate in PEM format
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        }
        else
        {
            // Export the certificate in DER format
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Cert));
        }

        File.WriteAllText(keyPath, keyPem);

        return certificate;
    }

    private static X509Certificate2 CreateTestCertificate(CertificateRequest request, SubjectAlternativeNameBuilder sanBuilder)
    {
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // Server Authentication

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static string ExportKeyToPem(AsymmetricAlgorithm key, string? password)
    {
        return password is null
            ? key.ExportPkcs8PrivateKeyPem()
            : key.ExportEncryptedPkcs8PrivateKeyPem(password.AsSpan(), new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
    }
}
