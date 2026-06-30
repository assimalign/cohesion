using System;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Quic;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Connections.Examples.Http3;

internal static class Program
{
    private static async Task<int> Main()
    {
        if (!IsSupportedPlatform() || !QuicConnection.IsSupported)
        {
            Console.Error.WriteLine("HTTP/3 example requires QUIC support on the current platform.");
            return 1;
        }

        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(20));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = LoopbackPortAllocator.AllocateUdpPort();
        Uri serverUri = new($"https://127.0.0.1:{port}/hello?name=http3");
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");

        await using HttpConnectionListener listener = await CreateListenerAsync(port, certificate, cancellationToken).ConfigureAwait(false);

        Task serverTask = RunServerAsync(listener, cancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        try
        {
            using HttpClientHandler handler = new()
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using HttpClient client = new(handler);
            using HttpRequestMessage request = new(ClientHttpMethod.Get, serverUri)
            {
                Version = NetHttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"HTTP/3 status: {(int)response.StatusCode}");
            Console.WriteLine($"HTTP/3 negotiated version: {response.Version}");
            Console.WriteLine(body);

            await serverTask.ConfigureAwait(false);

            return 0;
        }
        catch (HttpRequestException exception) when (exception.InnerException is AuthenticationException or null)
        {
            cancellationTokenSource.Cancel();

            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Console.Error.WriteLine("HTTP/3 request could not complete because the local QUIC/TLS handshake failed.");
            Console.Error.WriteLine(exception.Message);

            return 1;
        }
    }

    private static async Task RunServerAsync(HttpConnectionListener listener, CancellationToken cancellationToken)
    {
        try
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);
            IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                string payload = $"Hello from HTTP/3. Method={context.Request.Method}, Path={context.Request.Path}, Name={context.Request.Query["name"].Value}";
                byte[] buffer = Encoding.UTF8.GetBytes(payload);

                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
                await context.Response.Body.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                await connectionContext.SendAsync(context, cancellationToken).ConfigureAwait(false);
                await context.DisposeAsync().ConfigureAwait(false);
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static async Task<HttpConnectionListener> CreateListenerAsync(int port, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        // HTTP/3 over QUIC: the QUIC listener is inherently TLS-secured (its
        // capabilities report Security = Tls) and multiplexed, so it plugs
        // into the HTTP listener through the dedicated UseHttp3 registration.
        QuicConnectionListener quicListener = await QuicConnectionListener.CreateAsync(transport =>
        {
            transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
            transport.ServerAuthenticationOptions.ServerCertificate = certificate;
            transport.ServerAuthenticationOptions.ApplicationProtocols = [SslApplicationProtocol.Http3];
        }, cancellationToken).ConfigureAwait(false);

        return HttpConnectionListener.Create(options =>
        {
            options.UseHttp3(quicListener);
        });
    }

    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    private static bool IsSupportedPlatform()
    {
        return OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS();
    }
}
