using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The default server publishes the transport's client-fault report to the pipeline as
/// <see cref="IWebClientFaultFeature"/> (#1340). A request dispatched at its head can turn out to be
/// the client's fault while the application reads its body; the read throws, the transport latches the
/// status it answers with, and the feature reports it, so the pipeline's fault boundary, access log and
/// request decompression can tell the client's fault from the application's. On HTTP/2 and HTTP/3 (#1378)
/// the feature is installed on a request whose stream can still carry content, whatever its head declares,
/// and a malformed body, which resets the stream, is reported as <c>400</c>.
/// </summary>
public class WebApplicationServerClientFaultTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: A malformed chunked body should be reported to the pipeline as the 400 the transport sends")]
    public async Task ClientFault_OnMalformedChunkedBody_ShouldReportTheTransportStatus()
    {
        // Arrange — "zz" is not a chunk size (RFC 9112 §7.1).
        Observed observed = new();

        // Act
        string response = await ServeRawAsync(
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\nabc\r\n0\r\n\r\n",
            observed,
            configure: null);

        // Assert
        observed.FeaturePresent.ShouldBeTrue();
        observed.BeforeRead.ShouldBeNull();
        observed.ReadFailure.ShouldBeOfType<InvalidDataException>();
        observed.AfterRead.ShouldBe(CohesionHttpStatusCode.BadRequest);
        response.ShouldStartWith("HTTP/1.1 400");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: A body over the size cap should be reported to the pipeline as the 413 the transport sends")]
    public async Task ClientFault_OnBodyOverCap_ShouldReportTheTransportStatus()
    {
        // Arrange
        Observed observed = new();

        // Act
        string response = await ServeRawAsync(
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 32\r\n\r\n" + new string('a', 32),
            observed,
            configure: http1 => http1.Limits.MaxRequestBodySize = 16);

        // Assert
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(CohesionHttpStatusCode.RequestEntityTooLarge);
        response.ShouldStartWith("HTTP/1.1 413");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: A body the client cuts short by closing its side should cancel the exchange without a client fault")]
    [InlineData("Content-Length: 100\r\n\r\n{")]
    [InlineData("Transfer-Encoding: chunked\r\n\r\n10\r\nshort")]
    public async Task ClientFault_OnBodyCutShortByTheClient_ShouldCancelWithoutAClientFault(string framingAndBody)
    {
        // Arrange — the client writes part of the body its framing declares, then closes its sending
        // side. Connection loss after dispatch cancels the exchange, including incomplete EOF (#1439).
        Observed observed = new();

        // Act
        string response = await ServeRawAsync(
            "POST /widgets HTTP/1.1\r\nHost: localhost\r\n" + framingAndBody,
            observed,
            configure: null,
            closeSendingSide: true);

        // Assert
        observed.FeaturePresent.ShouldBeTrue();
        observed.BeforeRead.ShouldBeNull();
        OperationCanceledException canceled = observed.ReadFailure.ShouldBeAssignableTo<OperationCanceledException>()!;
        observed.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        canceled.CancellationToken.ShouldBe(observed.RequestCancelled);
        observed.AfterRead.ShouldBeNull();
        response.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: A well-formed body should report no client fault")]
    public async Task ClientFault_OnWellFormedBody_ShouldReportNone()
    {
        // Arrange
        Observed observed = new();

        // Act
        string response = await ServeRawAsync(
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nContent-Length: 3\r\n\r\nabc",
            observed,
            configure: null);

        // Assert
        observed.FeaturePresent.ShouldBeTrue();
        observed.ReadFailure.ShouldBeNull();
        observed.AfterRead.ShouldBeNull();
        response.ShouldStartWith("HTTP/1.1 204");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: A request without a body should keep the fast path and carry no client-fault feature")]
    public async Task ClientFault_OnRequestWithoutBody_ShouldNotInstallTheFeature()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        bool? featurePresent = null;

        await using WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            featurePresent = context.Features.Get<IWebClientFaultFeature>() is not null;
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/plain", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        featurePresent.ShouldBe(false);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: A malformed HTTP/2 body should be reported to the pipeline as a 400 client fault while the stream is reset")]
    [InlineData("content-length")]
    [InlineData("trailer")]
    public async Task ClientFault_OnMalformedHttp2Body_ShouldReportBadRequestAndResetTheStream(string fault)
    {
        // Arrange — the POST declares no body in its head; only its open stream says one may follow.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Observed observed = new();

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(transport));
        UseObservingPipeline(factory, observed);
        await factory.StartAsync(cancellationToken);
        await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);

        // Act — the stream ends after 16 of the 100 octets it declares (RFC 9113 §8.1.1), or a trailer
        // section carries a field no trailer may (RFC 9110 §6.5.1).
        if (fault == "content-length")
        {
            await client.SendHeadersAsync(1, "POST", "/upload", endStream: false, cancellationToken, ("content-length", "100"));
            await observed.BeforeReadRecorded.Task.WaitAsync(cancellationToken);
            await client.SendDataAsync(1, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"), endStream: true, cancellationToken);
        }
        else
        {
            await client.SendHeadersAsync(1, "POST", "/upload", endStream: false, cancellationToken);
            await observed.BeforeReadRecorded.Task.WaitAsync(cancellationToken);
            await client.SendDataAsync(1, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"), endStream: false, cancellationToken);
            await client.SendTrailersAsync(1, cancellationToken, ("content-length", "5"));
        }

        Http2RawFrame reset = await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.RstStreamType, cancellationToken);
        await observed.Recorded.Task.WaitAsync(cancellationToken);

        // Assert — the client gets PROTOCOL_ERROR, and the pipeline learns it was the client's fault.
        observed.FeaturePresent.ShouldBeTrue();
        observed.BeforeRead.ShouldBeNull();
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(CohesionHttpStatusCode.BadRequest);
        reset.RstStreamErrorCode.ShouldBe(0x1u);
        client.Frames.ShouldNotContain(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.HeadersType);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: An HTTP/2 body over the size cap should be reported to the pipeline as the 413 the transport sends")]
    public async Task ClientFault_OnHttp2BodyOverCap_ShouldReportTheTransportStatus()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Observed observed = new();

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(transport, http2 => http2.Limits.MaxRequestBodySize = 16));
        UseObservingPipeline(factory, observed);
        await factory.StartAsync(cancellationToken);
        await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);

        // Act — no content-length, so only the running total crosses the cap.
        await client.SendHeadersAsync(1, "POST", "/upload", endStream: false, cancellationToken);
        await client.SendDataAsync(1, new byte[32], endStream: false, cancellationToken);
        Http2RawFrame answer = await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.HeadersType, cancellationToken);
        await observed.Recorded.Task.WaitAsync(cancellationToken);

        // Assert — the transport's 413 is the stream's only response.
        observed.FeaturePresent.ShouldBeTrue();
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(CohesionHttpStatusCode.RequestEntityTooLarge);
        answer.EndStream.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: An HTTP/2 request whose head ends the stream should keep the fast path and carry no client-fault feature")]
    public async Task ClientFault_OnHttp2RequestWithoutBody_ShouldNotInstallTheFeature()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Observed observed = new();

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp2(transport));
        UseObservingPipeline(factory, observed);
        await factory.StartAsync(cancellationToken);
        await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);

        // Act
        await client.SendGetAsync(1, "/plain", cancellationToken);
        await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.Type == Http2RawFrame.HeadersType, cancellationToken);
        await observed.Recorded.Task.WaitAsync(cancellationToken);

        // Assert
        observed.FeaturePresent.ShouldBeFalse();
        observed.ReadFailure.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: A malformed HTTP/3 body should be reported to the pipeline as a 400 client fault while the stream is reset")]
    [InlineData("content-length")]
    [InlineData("trailer")]
    public async Task ClientFault_OnMalformedHttp3Body_ShouldReportBadRequestAndResetTheStream(string fault)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Observed observed = new();

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        UseObservingPipeline(factory, observed);
        await factory.StartAsync(cancellationToken);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);
        byte[] json = Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}");

        // Act — the stream ends after 16 of the 100 octets it declares (RFC 9114 §4.1.2), or a trailer
        // section carries a field no trailer may.
        Connection stream = fault == "content-length"
            ? await client.SendRequestAsync("POST", "/upload", [("content-length", "100")], [Http3RawClient.Data(json)], endStream: true, cancellationToken)
            : await client.SendRequestAsync("POST", "/upload", [], [Http3RawClient.Data(json), Http3RawClient.Trailers(("content-length", "5"))], endStream: true, cancellationToken);
        (IReadOnlyList<int> statuses, long? resetCode) = await Http3RawClient.ReadResponseAsync(stream, cancellationToken);
        await observed.Recorded.Task.WaitAsync(cancellationToken);

        // Assert — the client gets H3_MESSAGE_ERROR, and the pipeline learns it was the client's fault.
        observed.FeaturePresent.ShouldBeTrue();
        observed.BeforeRead.ShouldBeNull();
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(CohesionHttpStatusCode.BadRequest);
        statuses.ShouldBeEmpty();
        resetCode.ShouldBe(0x10eL);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: An HTTP/3 body over the size cap should be reported to the pipeline as the 413 the transport sends")]
    public async Task ClientFault_OnHttp3BodyOverCap_ShouldReportTheTransportStatus()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Observed observed = new();

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport, http3 => http3.Limits.MaxRequestBodySize = 16));
        UseObservingPipeline(factory, observed);
        await factory.StartAsync(cancellationToken);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);

        // Act — no Content-Length, so only the DATA frame crosses the cap.
        Connection stream = await client.SendRequestAsync("POST", "/upload", [], [Http3RawClient.Data(new byte[32])], endStream: false, cancellationToken);
        (IReadOnlyList<int> statuses, long? resetCode) = await Http3RawClient.ReadResponseAsync(stream, cancellationToken);
        await observed.Recorded.Task.WaitAsync(cancellationToken);

        // Assert
        observed.FeaturePresent.ShouldBeTrue();
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(CohesionHttpStatusCode.RequestEntityTooLarge);
        resetCode.ShouldBeNull();
        statuses.ShouldBe([413]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: DATA after an HTTP/3 trailer section should be reported to the pipeline as a 400 client fault")]
    public async Task ClientFault_OnHttp3DataAfterTrailerSection_ShouldReportBadRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Observed observed = new();

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        UseObservingPipeline(factory, observed);
        await factory.StartAsync(cancellationToken);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);
        byte[] json = Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}");

        // Act — RFC 9114 §4.1: only the stream's end may follow a trailer section, so the DATA frame behind it
        // is an invalid frame sequence, which closes the connection. The octets were the client's all the same.
        await client.SendRequestAsync(
            "POST",
            "/upload",
            [],
            [Http3RawClient.Data(json), Http3RawClient.Trailers(("x-checksum", "abc")), Http3RawClient.Data(json)],
            endStream: true,
            cancellationToken);
        await observed.Recorded.Task.WaitAsync(cancellationToken);

        // Assert
        observed.FeaturePresent.ShouldBeTrue();
        observed.BeforeRead.ShouldBeNull();
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(CohesionHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: An HTTP/3 request the client resets mid-body should end as a cancellation, not as an application fault")]
    public async Task ClientFault_OnHttp3ResetMidBody_ShouldEndTheExchangeAsCancelled()
    {
        // Arrange — the pipeline reads the body and lets the read's failure escape to the server, as a handler
        // that does not catch it does.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(Exception Failure, bool Cancelled, CohesionHttpStatusCode? Fault)> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        factory.Application.Use(async (context, next) =>
        {
            IWebClientFaultFeature? feature = context.Features.Get<IWebClientFaultFeature>();
            byte[] buffer = new byte[256];

            try
            {
                if (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
                {
                    firstRead.TrySetResult();
                }

                while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
                {
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                failed.TrySetResult((exception, context.RequestCancelled.IsCancellationRequested, feature?.StatusCode));
                throw;
            }
        });
        await factory.StartAsync(cancellationToken);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);

        // Act — RESET_STREAM(H3_REQUEST_CANCELLED) once the server has read the first DATA frame: the client
        // cancels its own request (RFC 9114 §4.1.1).
        Connection stream = await client.SendRequestAsync(
            "POST",
            "/upload",
            [],
            [Http3RawClient.Data(Encoding.ASCII.GetBytes("{\"name\":"))],
            endStream: false,
            cancellationToken);
        await firstRead.Task.WaitAsync(cancellationToken);
        ((IMultiplexedStreamAbort)stream).AbortWrite(0x10c);
        (Exception failure, bool cancelled, CohesionHttpStatusCode? fault) = await failed.Task.WaitAsync(cancellationToken);
        (IReadOnlyList<int> statuses, long? resetCode) = await Http3RawClient.ReadResponseAsync(stream, cancellationToken);

        // Assert — the read surfaces the cancellation with RequestCancelled already fired, which a fault
        // boundary passes through as one, and the server resets the stream instead of sending a 500.
        failure.ShouldBeAssignableTo<OperationCanceledException>();
        cancelled.ShouldBeTrue();
        fault.ShouldBeNull();
        statuses.ShouldBeEmpty();
        resetCode.ShouldBe(0x10cL);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Client fault: An HTTP/3 request whose stream ended with its head should keep the fast path and carry no client-fault feature")]
    public async Task ClientFault_OnHttp3RequestWithoutBody_ShouldNotInstallTheFeature()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        Observed observed = new();

        InMemoryMultiplexedConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp3(transport));
        UseObservingPipeline(factory, observed);
        await factory.StartAsync(cancellationToken);
        await using Http3RawClient client = await Http3RawClient.ConnectAsync(transport, cancellationToken);

        // Act — the GET's FIN goes out with its HEADERS frame. The in-memory driver reports it with the head;
        // the QUIC driver's completion facet exposes its observed FIN before the input pipe reads zero
        // (Http.Connections' HttpRequestCanHaveBodyTests pins the same gate over a stream-backed input).
        Connection stream = await client.SendRequestAsync("GET", "/plain", [], [], endStream: true, cancellationToken);
        (IReadOnlyList<int> statuses, _) = await Http3RawClient.ReadResponseAsync(stream, cancellationToken);
        await observed.Recorded.Task.WaitAsync(cancellationToken);

        // Assert
        observed.FeaturePresent.ShouldBeFalse();
        statuses.ShouldBe([204]);
    }

    /// <summary>
    /// Serves <paramref name="request"/> on one raw connection to a default Web server whose pipeline reads
    /// the request body to its end, recording the client-fault feature's report before and after the read
    /// and the read's failure, then stages <c>204</c>. With <paramref name="closeSendingSide"/> the client
    /// closes its sending side after the request, as a peer that cuts the body short does. Reads until the
    /// server closes the connection and returns everything it wrote.
    /// </summary>
    private static async Task<string> ServeRawAsync(
        string request,
        Observed observed,
        Action<Http1ConnectionListenerOptions>? configure,
        bool closeSendingSide = false)
    {
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport, http1 => configure?.Invoke(http1)));
        UseObservingPipeline(factory, observed);

        await factory.StartAsync(cancellationToken);

        await using Connection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = connection.AsStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        if (closeSendingSide)
        {
            await connection.Output.CompleteAsync();
        }

        StringBuilder received = new();
        byte[] chunk = new byte[1024];

        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                return received.ToString();
            }

            received.Append(Encoding.ASCII.GetString(chunk, 0, read));
        }
    }

    /// <summary>
    /// Composes a pipeline that reads the request body to its end, recording the client-fault feature's
    /// report before and after the read and the read's failure, then stages <c>204</c>.
    /// </summary>
    private static void UseObservingPipeline(WebApplicationTestFactory factory, Observed observed)
    {
        factory.Application.Use(async (context, next) =>
        {
            IWebClientFaultFeature? feature = context.Features.Get<IWebClientFaultFeature>();
            observed.FeaturePresent = feature is not null;
            observed.RequestCancelled = context.RequestCancelled;
            observed.BeforeRead = feature?.StatusCode;
            observed.BeforeReadRecorded.TrySetResult();

            try
            {
                byte[] buffer = new byte[256];

                while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
                {
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
            {
                observed.ReadFailure = exception;
            }

            observed.AfterRead = feature?.StatusCode;
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            observed.Recorded.TrySetResult();
        });
    }

    private sealed class Observed
    {
        public bool FeaturePresent { get; set; }

        public CancellationToken RequestCancelled { get; set; }

        public CohesionHttpStatusCode? BeforeRead { get; set; }

        public Exception? ReadFailure { get; set; }

        public CohesionHttpStatusCode? AfterRead { get; set; }

        /// <summary>Completes once the pipeline captured the status before the client sends its malformed body.</summary>
        public TaskCompletionSource BeforeReadRecorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the pipeline has recorded the exchange.</summary>
        public TaskCompletionSource Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

