using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Transports;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http.Transports.Examples.Http2;

internal static class Program
{
    private static async Task<int> Main()
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(20));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = 8080;
        Uri serverUri = new($"http://127.0.0.1:{port}/hello?name=http2");

        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp2(transport =>
            {
                transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
            });
        });

        await RunServerAsync(listener, cancellationToken);

        //await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        //using SocketsHttpHandler handler = new();
        //using HttpClient client = new(handler);
        //using HttpRequestMessage request = new(ClientHttpMethod.Get, serverUri)
        //{
        //    Version = NetHttpVersion.Version20,
        //    VersionPolicy = HttpVersionPolicy.RequestVersionExact
        //};

        //using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        //string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        //Console.WriteLine($"HTTP/2 status: {(int)response.StatusCode}");
        //Console.WriteLine($"HTTP/2 negotiated version: {response.Version}");
        //Console.WriteLine(body);

       // await serverTask.ConfigureAwait(false);

        return 0;
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

                byte[] b = new byte[context.Request.Body.Length];
                context.Request.Body.ReadExactly(b);

                string d = Encoding.UTF8.GetString(b);
                Console.WriteLine($"HTTP/2 Request Payload: {d}");

                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
                await context.Response.Body.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                await connectionContext.SendAsync(context, cancellationToken).ConfigureAwait(false);
                await context.DisposeAsync().ConfigureAwait(false);
                
               
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            throw;
        }
    }
}
