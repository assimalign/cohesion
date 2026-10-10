using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/1.1 request bodies rejected over a limit after dispatch (#1339). The request is dispatched
/// at its head, so a body over the size cap or below the minimum data rate is only found when the
/// application reads it. The transport then answers the limit itself, as HTTP/2 and HTTP/3 do:
/// <c>413</c> or <c>408</c> in place of a response that has not started, with
/// <c>Connection: close</c>, and the connection ends. The body is never read past the breach, so
/// the octets behind it are never served as a request.
/// </summary>
public class Http1RequestBodyRejectionTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Body Limits: A Content-Length body over the cap read by the application should be answered with 413 and close the connection")]
    public async Task ReceiveAsync_OnContentLengthBodyOverCap_ShouldAnswerContentTooLargeAndClose()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 32\r\n\r\n"
            + new string('a', 32)
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload, http1 => http1.Limits.MaxRequestBodySize = 16);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures[0].ShouldBeAssignableTo<IOException>();
        result.Output.ShouldStartWith("HTTP/1.1 413");
        result.Output.ShouldContain("Connection: close");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Body Limits: A chunk over the cap should be answered with 413 and its data never read as a next request")]
    public async Task ReceiveAsync_OnChunkOverCap_ShouldAnswerContentTooLargeAndNotServeTheChunkData()
    {
        // Arrange — the cap breaks at the chunk-size line, before the chunk's data. That data reads
        // like a last chunk and then a new request, which the original framing never delimited.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "40\r\n"
            + "0\r\n\r\nGET /smuggled HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(payload, http1 => http1.Limits.MaxRequestBodySize = 16);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures[0].ShouldBeAssignableTo<IOException>();
        result.Output.ShouldStartWith("HTTP/1.1 413");
        result.Output.ShouldContain("Connection: close");
        result.Output.ShouldNotContain("HTTP/1.1 200");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Body Limits: A body received below the minimum data rate should be answered with 408 and close the connection")]
    public async Task ReceiveAsync_OnBodyBelowMinimumDataRate_ShouldAnswerRequestTimeoutAndClose()
    {
        // Arrange — the head declares a body the peer never sends.
        byte[] head = Encoding.ASCII.GetBytes(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 100\r\n\r\n");

        // Act
        Served result = await ServeAsync(
            head,
            http1 => http1.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromMilliseconds(100)),
            completeInput: false);

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures[0].ShouldBeAssignableTo<IOException>();
        result.Output.ShouldStartWith("HTTP/1.1 408");
        result.Output.ShouldContain("Connection: close");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Body Limits: An application that answers the limit status itself should keep its response and close the connection")]
    public async Task ReceiveAsync_OnApplicationAnsweringTheLimitStatus_ShouldKeepItsResponseAndClose()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 32\r\n\r\n"
            + new string('a', 32)
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(
            payload,
            http1 => http1.Limits.MaxRequestBodySize = 16,
            onReadFailure: exchange =>
            {
                exchange.Response.StatusCode = HttpStatusCode.RequestEntityTooLarge;
                exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("upload too large"));
                return Task.CompletedTask;
            });

        // Assert
        result.Paths.ShouldBe(["/upload"]);
        result.Output.ShouldStartWith("HTTP/1.1 413");
        result.Output.ShouldContain("Connection: close");
        result.Output.ShouldEndWith("upload too large");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Body Limits: A response already started when the body is rejected should be finished and the connection closed")]
    public async Task ReceiveAsync_OnRejectionAfterResponseStarted_ShouldFinishResponseAndClose()
    {
        // Arrange — the application streams part of its response before it reads the body.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 32\r\n\r\n"
            + new string('a', 32)
            + "GET /next HTTP/1.1\r\nHost: api.test\r\n\r\n");

        // Act
        Served result = await ServeAsync(
            payload,
            http1 => http1.Limits.MaxRequestBodySize = 16,
            beforeRead: async exchange =>
            {
                await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("partial"));
                await exchange.Response.Streaming.FlushAsync();
            },
            streaming: true);

        // Assert — the streamed response is completed as it is; no second status line follows it.
        result.Paths.ShouldBe(["/upload"]);
        result.ReadFailures[0].ShouldBeAssignableTo<IOException>();
        result.Output.ShouldStartWith("HTTP/1.1 200");
        result.Output.ShouldNotContain("413");
        result.Output.ShouldEndWith("7\r\npartial\r\n0\r\n\r\n");
    }

    /// <summary>
    /// Serves every request on one connection the way a host does: runs <paramref name="beforeRead"/>,
    /// reads the body to its end, runs <paramref name="onReadFailure"/> when that read fails, then
    /// sends the response. Returns the paths served, each body read's failure, and everything the
    /// server wrote.
    /// </summary>
    private static async Task<Served> ServeAsync(
        byte[] payload,
        Action<Http1ConnectionListenerOptions> configure,
        Func<IHttpContext, Task>? beforeRead = null,
        Func<IHttpContext, Task>? onReadFailure = null,
        bool completeInput = true,
        bool streaming = false)
    {
        TestConnection transport = new(payload, completeInput: completeInput);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(transport), configure);

        if (streaming)
        {
            options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        }

        Served served = new();

        await using (HttpConnectionListener listener = new(options))
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync();
            IHttpConnectionContext connectionContext = await connection.OpenAsync();

            await foreach (IHttpContext exchange in connectionContext.ReceiveAsync())
            {
                served.Paths.Add(exchange.Request.Path.Value);

                if (beforeRead is not null)
                {
                    await beforeRead(exchange);
                }

                Exception? failure = await TryReadToEndAsync(exchange.Request.Body);
                served.ReadFailures.Add(failure);

                if (failure is not null && onReadFailure is not null)
                {
                    await onReadFailure(exchange);
                }

                await connectionContext.SendAsync(exchange);
            }
        }

        // The connection is disposed, so its output is complete: everything the server wrote is here.
        served.Output = Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return served;
    }

    private static async Task<Exception?> TryReadToEndAsync(Stream body)
    {
        try
        {
            byte[] buffer = new byte[256];
            while (await body.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) > 0)
            {
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class Served
    {
        public List<string> Paths { get; } = new();

        public List<Exception?> ReadFailures { get; } = new();

        public string Output { get; set; } = string.Empty;
    }
}
