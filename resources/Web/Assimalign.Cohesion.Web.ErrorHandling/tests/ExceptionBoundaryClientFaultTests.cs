using System;
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
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests;

/// <summary>
/// The exception boundary over the real Web server (#1340). A request body that breaks its framing or a
/// configured limit fails the application's read, and the transport answers the exchange itself. The
/// server reports the client fault, so the boundary neither observes it as an application fault nor
/// renders a <c>500</c> problem; the client gets the transport's status.
/// </summary>
public class ExceptionBoundaryClientFaultTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary E2E: A client fault found reading the body should reach the client as the transport's status without OnException")]
    [InlineData("Transfer-Encoding: chunked\r\n\r\nzz\r\nabc\r\n0\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("Content-Length: 32\r\n\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "HTTP/1.1 413")]
    public async Task Boundary_ClientFaultReadingTheBody_ShouldAnswerTheTransportStatusWithoutObserving(string framingAndBody, string expectedStatusLine)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int observed = 0;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport, http1 => http1.Limits.MaxRequestBodySize = 16));
        factory.Application.UseErrorHandling(options => options.OnException = (_, _) =>
        {
            Interlocked.Increment(ref observed);
            return ValueTask.CompletedTask;
        });
        factory.Application.Use(async (context, next) =>
        {
            byte[] buffer = new byte[256];

            while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
            }

            context.Response.StatusCode = HttpStatusCode.NoContent;
        });

        await factory.StartAsync(cancellationToken);

        // Act
        string response = await ExchangeRawAsync(transport, "POST /upload HTTP/1.1\r\nHost: localhost\r\n" + framingAndBody, cancellationToken);

        // Assert
        response.ShouldStartWith(expectedStatusLine);
        response.ShouldContain("Connection: close");
        response.ShouldNotContain("application/problem+json");
        response.ShouldNotContain("500");
        observed.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary E2E: A body the client cuts short by closing its side should reach the client as the transport's 400 without OnException")]
    [InlineData("Content-Length: 100\r\n\r\n{")]
    [InlineData("Transfer-Encoding: chunked\r\n\r\n10\r\nshort")]
    public async Task Boundary_BodyCutShortByTheClient_ShouldAnswerTheTransport400WithoutObserving(string framingAndBody)
    {
        // Arrange — the client writes part of the body its framing declares, then closes its sending side,
        // so the read throws EndOfStreamException: the cheapest way to make a read fail on demand.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int observed = 0;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport));
        factory.Application.UseErrorHandling(options => options.OnException = (_, _) =>
        {
            Interlocked.Increment(ref observed);
            return ValueTask.CompletedTask;
        });
        factory.Application.Use(async (context, next) =>
        {
            byte[] buffer = new byte[256];

            while (await context.Request.Body.ReadAsync(buffer, context.RequestCancelled) > 0)
            {
            }

            context.Response.StatusCode = HttpStatusCode.NoContent;
        });

        await factory.StartAsync(cancellationToken);

        // Act
        string response = await ExchangeRawAsync(
            transport,
            "POST /widgets HTTP/1.1\r\nHost: localhost\r\n" + framingAndBody,
            cancellationToken,
            closeSendingSide: true);

        // Assert
        response.ShouldStartWith("HTTP/1.1 400");
        response.ShouldContain("Connection: close");
        response.ShouldNotContain("application/problem+json");
        response.ShouldNotContain("500");
        observed.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Boundary E2E: An application fault reading a well-formed body should still be observed and answered 500")]
    public async Task Boundary_ApplicationFaultAfterAWellFormedBody_ShouldObserveAndRender500()
    {
        // Arrange — the body is fine; the application itself fails, so the boundary owns the fault.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        int observed = 0;

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport));
        factory.Application.UseErrorHandling(options => options.OnException = (_, _) =>
        {
            Interlocked.Increment(ref observed);
            return ValueTask.CompletedTask;
        });
        factory.Application.Use(async (context, next) =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestCancelled);
            throw new InvalidOperationException("Deliberate application fault.");
        });

        await factory.StartAsync(cancellationToken);

        // Act
        string response = await ExchangeRawAsync(
            transport,
            "POST /upload HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nContent-Length: 3\r\n\r\nabc",
            cancellationToken);

        // Assert
        response.ShouldStartWith("HTTP/1.1 500");
        response.ShouldContain("application/problem+json");
        observed.ShouldBe(1);
    }

    /// <summary>
    /// Writes <paramref name="request"/> on one raw connection to <paramref name="transport"/> and reads
    /// until the server closes the connection, returning everything it wrote. With
    /// <paramref name="closeSendingSide"/> the client closes its sending side after the request.
    /// </summary>
    private static async Task<string> ExchangeRawAsync(
        InMemoryConnectionListener transport,
        string request,
        CancellationToken cancellationToken,
        bool closeSendingSide = false)
    {
        await using Connection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = connection.AsStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        if (closeSendingSide)
        {
            await connection.Output.CompleteAsync();
        }

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
}
