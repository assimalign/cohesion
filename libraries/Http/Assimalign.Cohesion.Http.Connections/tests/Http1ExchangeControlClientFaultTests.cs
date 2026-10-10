using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/1.1 exchange control's client-fault report (#1340). A body read after dispatch that fails
/// on the client's side throws, and the transport latches the status it answers with: <c>400</c> for
/// malformed chunked framing (#1333), <c>413</c> over the body-size cap and <c>408</c> below the minimum
/// data rate (#1339). <see cref="IHttpExchangeControl.ClientFaultStatusCode"/> reports that status to the
/// code that observes the exception, so it can tell the client's fault from the application's.
/// </summary>
public class Http1ExchangeControlClientFaultTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Client Fault: A malformed chunked body should be reported as a 400 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnMalformedChunkedBody_ShouldReportBadRequest()
    {
        // Arrange — "zz" is not a chunk size (RFC 9112 §7.1).
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "zz\r\nabc\r\n0\r\n\r\n");

        // Act
        Observed observed = await ServeAsync(payload, http1 => { });

        // Assert
        observed.BeforeRead.ShouldBeNull();
        observed.ReadFailure.ShouldBeOfType<InvalidDataException>();
        observed.AfterRead.ShouldBe(HttpStatusCode.BadRequest);
        observed.Output.ShouldStartWith("HTTP/1.1 400");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Client Fault: A body over the size cap should be reported as a 413 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnBodyOverCap_ShouldReportContentTooLarge()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 32\r\n\r\n" + new string('a', 32));

        // Act
        Observed observed = await ServeAsync(payload, http1 => http1.Limits.MaxRequestBodySize = 16);

        // Assert
        observed.BeforeRead.ShouldBeNull();
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        observed.Output.ShouldStartWith("HTTP/1.1 413");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Client Fault: A body below the minimum data rate should be reported as a 408 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnBodyBelowMinimumDataRate_ShouldReportRequestTimeout()
    {
        // Arrange — the head declares a body the peer never sends.
        byte[] head = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nContent-Length: 100\r\n\r\n");

        // Act
        Observed observed = await ServeAsync(
            head,
            http1 => http1.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromMilliseconds(100)),
            completeInput: false);

        // Assert
        observed.ReadFailure.ShouldBeAssignableTo<IOException>();
        observed.AfterRead.ShouldBe(HttpStatusCode.RequestTimeout);
        observed.Output.ShouldStartWith("HTTP/1.1 408");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http1 Client Fault: A well-formed body should report no client fault")]
    public async Task ClientFaultStatusCode_OnWellFormedChunkedBody_ShouldReportNull()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "POST /upload HTTP/1.1\r\nHost: api.test\r\nTransfer-Encoding: chunked\r\n\r\n"
            + "3\r\nabc\r\n0\r\n\r\n");

        // Act
        Observed observed = await ServeAsync(payload, http1 => { });

        // Assert
        observed.ReadFailure.ShouldBeNull();
        observed.AfterRead.ShouldBeNull();
        observed.Output.ShouldStartWith("HTTP/1.1 200");
    }

    /// <summary>
    /// Serves the one request on a connection whose listener registers a response interceptor that
    /// captures the exchange control, reads the request body to its end, and sends the response. Returns
    /// the control's report before and after the read, the read's failure, and everything the server
    /// wrote.
    /// </summary>
    private static async Task<Observed> ServeAsync(
        byte[] payload,
        Action<Http1ConnectionListenerOptions> configure,
        bool completeInput = true)
    {
        TestConnection transport = new(payload, completeInput: completeInput);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(transport), configure);
        options.Interceptors.Add(new ControlCapturingInterceptor());

        Observed observed = new();

        await using (HttpConnectionListener listener = new(options))
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync();
            IHttpConnectionContext connectionContext = await connection.OpenAsync();

            await foreach (IHttpContext exchange in connectionContext.ReceiveAsync())
            {
                IHttpExchangeControl control = exchange.Features.Get<CapturedControlFeature>().ShouldNotBeNull().Control;

                observed.BeforeRead = control.ClientFaultStatusCode;
                observed.ReadFailure = await TryReadToEndAsync(exchange.Request.Body);
                observed.AfterRead = control.ClientFaultStatusCode;

                await connectionContext.SendAsync(exchange);
            }
        }

        observed.Output = System.Text.Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return observed;
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

    private sealed class Observed
    {
        public HttpStatusCode? BeforeRead { get; set; }

        public Exception? ReadFailure { get; set; }

        public HttpStatusCode? AfterRead { get; set; }

        public string Output { get; set; } = string.Empty;
    }

    /// <summary>
    /// Takes every exchange into the response phase and publishes its control as a feature.
    /// </summary>
    private sealed class ControlCapturingInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            context.Features.Set(new CapturedControlFeature(context.Control.ShouldNotBeNull()));
        }
    }

    private sealed class CapturedControlFeature : IHttpFeature
    {
        public CapturedControlFeature(IHttpExchangeControl control)
        {
            Control = control;
        }

        public string Name => nameof(CapturedControlFeature);

        public IHttpExchangeControl Control { get; }
    }
}
