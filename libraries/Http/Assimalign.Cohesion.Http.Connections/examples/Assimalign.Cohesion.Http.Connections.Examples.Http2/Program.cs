using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Security;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Connections.Examples.Http2;

internal static class Program
{
    private static async Task<int> Main()
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(20));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = LoopbackPortAllocator.AllocateTcpPort();
        Uri serverUri = new($"https://127.0.0.1:{port}/hello?name=http2");
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");

        // HTTP/2 over TLS: the TCP listener is composed with a TLS layer at
        // the root (ALPN "h2"), then registered with the HTTP listener. The
        // layered listener reports Security = Tls on its capabilities, so the
        // HTTP layer derives the https scheme without a separate hint.
        IConnectionListener securedListener = TcpConnectionListener
            .Create(transport =>
            {
                transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
            })
            .UseTls(new TlsServerOptions
            {
                AuthenticationOptions =
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http2]
                }
            });

        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp2(securedListener);
        });

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
                Version = NetHttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"HTTP/2 status: {(int)response.StatusCode}");
            Console.WriteLine($"HTTP/2 negotiated version: {response.Version}");
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

            Console.Error.WriteLine("HTTP/2 request could not complete because the local TLS handshake failed.");
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
                string payload = $"Hello from HTTP/2. Method={context.Request.Method}, Path={context.Request.Path}, Name={context.Request.Query["name"].Value}";
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
}
