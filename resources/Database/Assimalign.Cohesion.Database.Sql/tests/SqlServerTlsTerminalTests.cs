using System;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlServerTlsTerminalTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Client] - TLS missing databases retain their terminal code for at least 200 refusals",
        Skip = "#1086 (Connections owner): TlsConnection completes SslStream without completing inner TCP output; Database-only fixes still lose terminal errors.")]
    public async Task ConnectAsync_RepeatedUnknownDatabaseOverTls_ShouldPreserveTerminalCode()
    {
        // Arrange: exercise the production Connections.Security decorator over real TCP.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
        var tcp = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var listener = tcp.UseTls(new()
        {
            AuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
        });
        await using var engine = SqlDatabaseEngine.Create("tls-terminal", new());
        await using var server = SqlDatabaseServer.Create(engine, new()
        {
            Listener = listener, ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
        });
        await server.StartAsync(timeout.Token);
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "absent", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = new TcpConnectionFactory().UseTls(new()
            {
                AuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
            }),
        });
        var elapsed = Stopwatch.StartNew();
        int refusals = 0;

        // Act: exercise teardown concurrently with the peer receiving its terminal error.
        do
        {
            var error = await Should.ThrowAsync<SqlClientException>(async () => await client.ConnectAsync(timeout.Token));
            error.Code.ShouldBe(ProtocolErrorCode.DatabaseNotFound, error.Message);
            refusals++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || refusals < 200) && elapsed.Elapsed < TimeSpan.FromSeconds(45));

        // Assert
        refusals.ShouldBeGreaterThanOrEqualTo(200);
    }
}