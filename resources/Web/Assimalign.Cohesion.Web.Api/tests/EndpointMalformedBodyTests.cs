using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Api.Tests.TestObjects;
using Assimalign.Cohesion.Web.Diagnostics;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// A typed endpoint whose request body breaks the HTTP/1.1 chunked framing (#1340). The transport's body
/// stream throws <see cref="InvalidDataException"/> and answers the exchange <c>400</c> itself (#1333).
/// The generated binding answers it <c>400</c> with a problem body, whether the body is bound from JSON
/// or from a form, and the HTTP logging middleware records the exchange as a client fault at its
/// configured level rather than as an application defect at <c>Error</c>. On HTTP/2 and HTTP/3 (#1378) a
/// body that contradicts its content-length, or a malformed trailer section, resets the stream instead; the
/// read throws an <see cref="IOException"/> the binding does not catch, and the exception boundary and the
/// access log still treat it as the client's fault.
/// </summary>
public class EndpointMalformedBodyTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Malformed body: A JSON-bound endpoint should answer a malformed chunked body 400 and log a client fault")]
    public async Task Binding_JsonBodyWithMalformedChunkedFraming_ShouldAnswerBadRequestAndLogAClientFault()
    {
        // Arrange — the first chunk is well formed; "zz" is not a chunk size (RFC 9112 §7.1).
        using CancellationTokenSource cancellation = new(_testTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        bool handlerRan = false;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport));
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddJsonSerialization(ApiTestJsonContext.Default);

        factory.Application.UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category));
        factory.Application.UseRouting();
        factory.Application.MapPost("/widgets", (Widget widget) =>
        {
            handlerRan = true;
            return widget.Name;
        });

        // Act
        string response = await ExchangeRawAsync(
            factory,
            transport,
            "POST /widgets HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "6\r\n{\"name\r\nzz\r\n\":\"gizmo\"}\r\n0\r\n\r\n",
            cancellation.Token);

        // Assert — the binding's problem body survives, since the transport answers the same status.
        handlerRan.ShouldBeFalse();
        response.ShouldStartWith("HTTP/1.1 400");
        response.ShouldContain("Connection: close");
        response.ShouldContain("application/problem+json");
        response.ShouldContain("$body", Case.Sensitive);
        response.ShouldContain("The request body could not be read.", Case.Sensitive);

        ILoggerEntry entry = await WaitForEntryAsync(recorded, cancellation.Token);
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Exception.ShouldBeNull();
        entry.Attributes[HttpLoggingAttributes.ResponseStatusCode].ShouldBe(400);
        entry.Attributes[HttpLoggingAttributes.ClientFault].ShouldBe(true);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Malformed body: A form-bound endpoint should answer a malformed chunked body 400 and log a client fault")]
    public async Task Binding_FormWithMalformedChunkedFraming_ShouldAnswerBadRequestAndLogAClientFault()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        bool handlerRan = false;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport));
        factory.Builder.Services.AddRouting();

        factory.Application.UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category));
        factory.Application.UseRouting();
        factory.Application.MapPost("/upload", ([FromForm] string title) =>
        {
            handlerRan = true;
            return title;
        });

        // Act
        string response = await ExchangeRawAsync(
            factory,
            transport,
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/x-www-form-urlencoded\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "4\r\ntitl\r\nzz\r\ne=boxes\r\n0\r\n\r\n",
            cancellation.Token);

        // Assert
        handlerRan.ShouldBeFalse();
        response.ShouldStartWith("HTTP/1.1 400");
        response.ShouldContain("Connection: close");
        response.ShouldContain("application/problem+json");
        response.ShouldContain("$form", Case.Sensitive);

        ILoggerEntry entry = await WaitForEntryAsync(recorded, cancellation.Token);
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Exception.ShouldBeNull();
        entry.Attributes[HttpLoggingAttributes.ResponseStatusCode].ShouldBe(400);
        entry.Attributes[HttpLoggingAttributes.ClientFault].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Api] - Malformed body: A JSON-bound endpoint should treat a malformed HTTP/2 body as a client fault, not an application fault")]
    [InlineData("content-length")]
    [InlineData("trailer")]
    public async Task Binding_JsonBodyMalformedOverHttp2_ShouldLogAClientFaultWithoutOnException(string fault)
    {
        // Arrange — #1378: the stream ends short of its declared content-length (RFC 9113 §8.1.1), or a
        // trailer section carries a field no trailer may, so the transport resets the stream while the
        // binding reads the body.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using MalformedBodyHarness harness = new();

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(transport));
        harness.Compose(factory);
        await factory.StartAsync(cancellationToken);
        await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);
        byte[] json = Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}");

        // Act
        if (fault == "content-length")
        {
            await client.SendHeadersAsync(1, "POST", "/widgets", endStream: false, cancellationToken, ("content-type", "application/json"), ("content-length", "100"));
            await client.SendDataAsync(1, json, endStream: true, cancellationToken);
        }
        else
        {
            await client.SendHeadersAsync(1, "POST", "/widgets", endStream: false, cancellationToken, ("content-type", "application/json"));
            await client.SendDataAsync(1, json, endStream: false, cancellationToken);
            await client.SendTrailersAsync(1, cancellationToken, ("content-length", "5"));
        }

        Http2RawFrame reset = await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.RstStreamType, cancellationToken);
        ILoggerEntry entry = await WaitForEntryAsync(harness.Recorded, cancellationToken);

        // Assert — PROTOCOL_ERROR on the wire; to the pipeline, the client's fault.
        reset.RstStreamErrorCode.ShouldBe(0x1u);
        harness.AssertClientFault(entry);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Api] - Malformed body: A JSON-bound endpoint should treat a malformed HTTP/3 body as a client fault, not an application fault")]
    [InlineData("content-length")]
    [InlineData("trailer")]
    public async Task Binding_JsonBodyMalformedOverHttp3_ShouldLogAClientFaultWithoutOnException(string fault)
    {
        // Arrange — #1378: as over HTTP/2, against RFC 9114 §4.1.2.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        using MalformedBodyHarness harness = new();

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        harness.Compose(factory);
        await factory.StartAsync(cancellationToken);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);
        byte[] json = Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}");

        // Act
        Connection stream = fault == "content-length"
            ? await client.SendRequestAsync(
                "POST",
                "/widgets",
                [("content-type", "application/json"), ("content-length", "100")],
                [Http3RawClient.Data(json)],
                endStream: true,
                cancellationToken)
            : await client.SendRequestAsync(
                "POST",
                "/widgets",
                [("content-type", "application/json")],
                [Http3RawClient.Data(json), Http3RawClient.Trailers(("content-length", "5"))],
                endStream: true,
                cancellationToken);
        (_, long? resetCode) = await Http3RawClient.ReadResponseAsync(stream, cancellationToken);
        ILoggerEntry entry = await WaitForEntryAsync(harness.Recorded, cancellationToken);

        // Assert — H3_MESSAGE_ERROR on the wire; to the pipeline, the client's fault.
        resetCode.ShouldBe(0x10eL);
        harness.AssertClientFault(entry);
    }

    /// <summary>
    /// Waits for the HTTP logging middleware's exchange entry, which it writes as the pipeline unwinds and
    /// can race the client reading the response.
    /// </summary>
    private static async Task<ILoggerEntry> WaitForEntryAsync(RecordingLoggerProvider recorded, CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<ILoggerEntry> entries = recorded.Entries;

            if (entries.Count > 0)
            {
                return entries[0];
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>
    /// Starts the factory, writes <paramref name="request"/> on one raw connection to
    /// <paramref name="transport"/>, and reads until the server closes the connection. Returns everything
    /// the server wrote.
    /// </summary>
    private static async Task<string> ExchangeRawAsync(
        WebApplicationTestFactory factory,
        InMemoryConnectionListener transport,
        string request,
        CancellationToken cancellationToken)
    {
        await factory.StartAsync(cancellationToken);

        await using Connection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = connection.AsStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        StringBuilder received = new();
        byte[] buffer = new byte[1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                return received.ToString();
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    /// <summary>
    /// A JSON-bound endpoint behind the HTTP access log and the exception boundary, recording whether the
    /// handler ran, what the access log wrote, and whether the boundary's fault observer saw anything.
    /// </summary>
    private sealed class MalformedBodyHarness : IDisposable
    {
        private int _observed;
        private ILoggerFactory? _loggerFactory;

        public RecordingLoggerProvider Recorded { get; } = new();

        public bool HandlerRan { get; private set; }

        public void Compose(WebApplicationTestFactory factory)
        {
            ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(Recorded).Build();
            _loggerFactory = loggerFactory;

            factory.Builder.Services.AddRouting();
            factory.Builder.Services.AddJsonSerialization(ApiTestJsonContext.Default);

            factory.Application.UseHttpLogging(loggerFactory.Create(new HttpLoggingOptions().Category));
            factory.Application.UseErrorHandling(options => options.OnException = (_, _) =>
            {
                Interlocked.Increment(ref _observed);
                return ValueTask.CompletedTask;
            });
            factory.Application.UseRouting();
            factory.Application.MapPost("/widgets", (Widget widget) =>
            {
                HandlerRan = true;
                return widget.Name;
            });
        }

        /// <summary>
        /// Asserts that the exchange was handled as the client's fault: the handler never ran, the boundary
        /// observed nothing, and the access log recorded the client fault's 400 at its configured level,
        /// without an exception.
        /// </summary>
        public void AssertClientFault(ILoggerEntry entry)
        {
            HandlerRan.ShouldBeFalse();
            Volatile.Read(ref _observed).ShouldBe(0);
            entry.Level.ShouldBe(LogLevel.Information);
            entry.Exception.ShouldBeNull();
            entry.Attributes[HttpLoggingAttributes.ResponseStatusCode].ShouldBe(400);
            entry.Attributes[HttpLoggingAttributes.ClientFault].ShouldBe(true);
        }

        public void Dispose()
        {
            _loggerFactory?.Dispose();
        }
    }
}
