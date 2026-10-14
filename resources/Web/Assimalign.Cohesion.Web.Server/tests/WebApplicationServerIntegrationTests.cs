using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Server.Tests.TestObjects;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Server.Tests;

/// <summary>
/// Full-pipeline integration coverage for the <c>WebApplicationServer</c> dispatch and stop
/// semantics (issues #762 and #1049), driven end to end over the in-memory transport through
/// the in-memory transport. The unit suite pins the same properties against
/// instrumented doubles; this suite proves them with a real client, real HTTP/1.1 exchanges,
/// and the real accept loop. The pipelining test writes raw bytes instead, because a real client
/// never pipelines — and pipelining is what shows the server asks for the next request only after
/// the previous response.
/// </summary>
public class WebApplicationServerIntegrationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Server: Pipelined HTTP/1.1 requests should be served in order, the next only after the previous response")]
    public async Task Server_PipelinedHttp1Requests_ShouldBeServedInOrderOneAtATime()
    {
        // Arrange — two requests pipelined in one write on one raw in-memory connection. /first parks
        // in the pipeline; an HTTP/1.1 connection carries one exchange at a time, so /second must not
        // reach the pipeline until /first's response has been written.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using InMemoryConnectionListener transport = new();
        IHttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp1(transport));
        FakePipeline pipeline = new(async (context, _) =>
        {
            string path = context.Request.Path.ToString();

            if (path == "/first")
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondEntered.TrySetResult();
            }

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(path), cancellationToken);
        });

        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = pipeline,

        }, listener);

        await server.StartAsync(cancellationToken);

        try
        {
            await using IDuplexConnection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
            Stream stream = client.AsStream();

            // Act
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "GET /first HTTP/1.1\r\nHost: localhost\r\n\r\n" +
                    "GET /second HTTP/1.1\r\nHost: localhost\r\n\r\n"),
                cancellationToken);
            await stream.FlushAsync(cancellationToken);

            await firstEntered.Task.WaitAsync(cancellationToken);
            await Task.Delay(200, cancellationToken);

            // Assert — /second is still waiting behind /first...
            secondEntered.Task.IsCompleted.ShouldBeFalse();

            releaseFirst.TrySetResult();

            // ...and the responses arrive in request order once /first is released.
            string responses = await ReadUntilAsync(stream, text => text.EndsWith("/second", StringComparison.Ordinal), cancellationToken);

            responses.Split("HTTP/1.1 200").Length.ShouldBe(3);
            responses.IndexOf("/first", StringComparison.Ordinal).ShouldBeLessThan(responses.IndexOf("/second", StringComparison.Ordinal));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Server: A response header that would split the response should be answered with 500 and the connection should keep serving")]
    public async Task Server_ResponseHeaderWithLineBreak_ShouldAnswer500AndKeepServing()
    {
        // Arrange — /inject reflects request text with a CRLF into a header, the response-splitting shape
        // (#1183, CWE-113). Two requests are pipelined on one raw connection, so the second shows the
        // connection was left aligned.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using InMemoryConnectionListener transport = new();
        IHttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp1(transport));
        FakePipeline pipeline = new(async (context, _) =>
        {
            string path = context.Request.Path.ToString();

            if (path == "/inject")
            {
                context.Response.Headers[new Assimalign.Cohesion.Http.HttpHeaderKey("Location")] = "/home\r\nSet-Cookie: session=attacker";
                context.Response.StatusCode = CohesionHttpStatusCode.Found;
            }

            await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(path), cancellationToken);
        });

        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = pipeline,

        }, listener);

        await server.StartAsync(cancellationToken);

        try
        {
            await using IDuplexConnection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
            Stream stream = client.AsStream();

            // Act
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "GET /inject HTTP/1.1\r\nHost: localhost\r\n\r\n" +
                    "GET /next HTTP/1.1\r\nHost: localhost\r\n\r\n"),
                cancellationToken);
            await stream.FlushAsync(cancellationToken);

            string responses = await ReadUntilAsync(stream, text => text.EndsWith("/next", StringComparison.Ordinal), cancellationToken);

            // Assert — no injected field, a bare 500 in place of the refused 302, then the next response.
            responses.ShouldStartWith("HTTP/1.1 500");
            responses.ShouldNotContain("Set-Cookie");
            responses.ShouldNotContain("/home");
            responses.ShouldContain("HTTP/1.1 200");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }


    private static async Task<string> ReadUntilAsync(Stream stream, Func<string, bool> isComplete, CancellationToken cancellationToken)
    {
        StringBuilder received = new();
        byte[] buffer = new byte[1024];

        while (!isComplete(received.ToString()))
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return received.ToString();
    }
}
