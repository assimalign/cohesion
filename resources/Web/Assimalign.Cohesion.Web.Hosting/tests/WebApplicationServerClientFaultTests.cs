using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
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
/// request decompression can tell the client's fault from the application's.
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

    /// <summary>
    /// Serves <paramref name="request"/> on one raw connection to a default Web server whose pipeline reads
    /// the request body to its end, recording the client-fault feature's report before and after the read
    /// and the read's failure, then answers <c>204</c>. Reads until the server closes the connection and
    /// returns everything it wrote.
    /// </summary>
    private static async Task<string> ServeRawAsync(string request, Observed observed, Action<Http1ConnectionListenerOptions>? configure)
    {
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport, http1 => configure?.Invoke(http1)));
        factory.Application.Use(async (context, next) =>
        {
            IWebClientFaultFeature? feature = context.Features.Get<IWebClientFaultFeature>();
            observed.FeaturePresent = feature is not null;
            observed.BeforeRead = feature?.StatusCode;

            try
            {
                byte[] buffer = new byte[256];

                while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
                {
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                observed.ReadFailure = exception;
            }

            observed.AfterRead = feature?.StatusCode;
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
        });

        await factory.StartAsync(cancellationToken);

        await using Connection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = connection.AsStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);

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

    private sealed class Observed
    {
        public bool FeaturePresent { get; set; }

        public CohesionHttpStatusCode? BeforeRead { get; set; }

        public Exception? ReadFailure { get; set; }

        public CohesionHttpStatusCode? AfterRead { get; set; }
    }
}
