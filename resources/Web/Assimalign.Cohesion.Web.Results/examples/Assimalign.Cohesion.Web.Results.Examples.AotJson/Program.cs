using System;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Routing;

using ClientHttpMethod = System.Net.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Results.Examples.AotJson;

/// <summary>
/// End-to-end NativeAOT evidence for the IResult foundation (#864): a representative Web.Api
/// endpoint — a <c>MapGet</c> route handler registered through the real router — returns
/// <c>Results.Ok&lt;T&gt;</c> serialized through a source-generated <c>JsonTypeInfo&lt;T&gt;</c>
/// (zero reflection), plus <c>Results.Problem</c> through the hand-rolled RFC 9457 writer. The
/// pipeline executes each result over the real HTTP/1.1 transport on localhost and a real client
/// asserts both payloads. The process exits non-zero on any mismatch, so a published native binary
/// is self-verifying.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(15));
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        int port = LoopbackPortAllocator.AllocateTcpPort();

        // The representative Web.Api endpoint surface: real routing feature, real UseRouting
        // middleware, real MapGet handler — returning IResult through the execution glue.
        MinimalWebApplication app = new();
        app.AddRouting();
        app.UseRouting();
        app.MapGet("/widgets/{id:int}", context =>
            context.ExecuteResultAsync(Results.Ok(new WidgetPayload("widget", 42), AotJsonContext.Default.WidgetPayload)));
        app.MapGet("/broken", context =>
            context.ExecuteResultAsync(Results.Problem(detail: "It broke.", statusCode: CohesionHttpStatusCode.ServiceUnavailable)));

        IWebApplicationPipeline pipeline = ((IWebApplicationPipelineBuilder)app).Build();

        // Plain (no TLS) HTTP/1.1 — a TCP listener composed directly into the HTTP listener.
        TcpConnectionListener tcpListener = TcpConnectionListener.Create(transport =>
        {
            transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        });

        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp1(tcpListener);
        });

        Task serverTask = RunServerAsync(listener, pipeline, cancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        using HttpClient client = new();

        string okBody = await GetAsync(client, $"http://127.0.0.1:{port}/widgets/42", 200, "application/json; charset=utf-8", cancellationToken).ConfigureAwait(false);
        string problemBody = await GetAsync(client, $"http://127.0.0.1:{port}/broken", 503, "application/problem+json", cancellationToken).ConfigureAwait(false);

        await serverTask.ConfigureAwait(false);

        // Self-verify so the published native binary is its own evidence.
        const string expectedOk = "{\"Name\":\"widget\",\"Count\":42}";
        if (okBody != expectedOk)
        {
            Console.Error.WriteLine($"FAIL: Ok<T> body mismatch. Expected {expectedOk}, got {okBody}");
            return 1;
        }

        if (!problemBody.Contains("\"status\":503", StringComparison.Ordinal) ||
            !problemBody.Contains("\"detail\":\"It broke.\"", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"FAIL: problem+json body mismatch. Got {problemBody}");
            return 1;
        }

        Console.WriteLine("PASS: Ok<T> JSON and problem+json rendered correctly under the current runtime.");
        return 0;
    }

    private static async Task<string> GetAsync(
        HttpClient client,
        string url,
        int expectedStatus,
        string expectedContentType,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(ClientHttpMethod.Get, url)
        {
            Version = NetHttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"GET {url}");
        Console.WriteLine($"  status:       {(int)response.StatusCode} (expected {expectedStatus})");
        Console.WriteLine($"  content-type: {response.Content.Headers.ContentType} (expected {expectedContentType})");
        Console.WriteLine($"  body:         {body}");

        if ((int)response.StatusCode != expectedStatus)
        {
            throw new InvalidOperationException($"Expected status {expectedStatus} from {url}, got {(int)response.StatusCode}.");
        }

        string? contentType = response.Content.Headers.ContentType?.ToString();
        if (contentType != expectedContentType)
        {
            throw new InvalidOperationException($"Expected content type '{expectedContentType}' from {url}, got '{contentType}'.");
        }

        return body;
    }

    private static async Task RunServerAsync(
        HttpConnectionListener listener,
        IWebApplicationPipeline pipeline,
        CancellationToken cancellationToken)
    {
        // Serve exactly two exchanges, whether the client rides one keep-alive connection
        // (the HttpClient pooling default) or opens one connection per request.
        int served = 0;
        while (served < 2)
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);
            IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                await pipeline.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                await connectionContext.SendAsync(context, cancellationToken).ConfigureAwait(false);
                await context.DisposeAsync().ConfigureAwait(false);

                if (++served == 2)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>The representative open DTO returned by the endpoint.</summary>
internal sealed record WidgetPayload(string Name, int Count);

/// <summary>
/// The source-generated serialization context — the zero-reflection <c>JsonTypeInfo&lt;T&gt;</c>
/// supply an endpoint author provides to <c>Results.Ok&lt;T&gt;</c> / <c>Results.Json&lt;T&gt;</c>.
/// </summary>
[JsonSerializable(typeof(WidgetPayload))]
internal partial class AotJsonContext : JsonSerializerContext
{
}
