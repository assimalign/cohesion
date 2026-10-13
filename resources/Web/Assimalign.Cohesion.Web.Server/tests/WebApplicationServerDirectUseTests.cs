using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Server.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Server.Tests;

[Collection(nameof(TelemetryCollection))]
public sealed class WebApplicationServerDirectUseTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Server] - Direct server: Serves a request with client-fault, response-starting and request-id features")]
    public async Task StartAsync_DirectConstruction_ShouldServeWithItsOwnFeatures()
    {
        // Arrange — this test project references no hosting module or hosting-based test factory.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using InMemoryConnectionListener transport = new();
        ActivityTraceId expectedId = ActivityTraceId.CreateRandom();
        int startingCalls = 0;
        bool completionCalled = false;
        WebApplicationServerOptions options = new()
        {
            Pipeline = new FakePipeline(async (context, token) =>
            {
                IWebClientFaultFeature fault = context.Features.Get<IWebClientFaultFeature>().ShouldNotBeNull();
                fault.StatusCode.ShouldBeNull();
                IWebRequestIdFeature requestId = context.Features.Get<IWebRequestIdFeature>().ShouldNotBeNull();
                requestId.RequestId.ShouldBe(expectedId);
                requestId.RequestId.ShouldBe(requestId.RequestId);
                IWebResponseStartingFeature starting = context.Features.Get<IWebResponseStartingFeature>().ShouldNotBeNull();
                starting.OnStarting(_ =>
                {
                    Interlocked.Increment(ref startingCalls);
                    context.Response.Headers[new HttpHeaderKey("X-Request-Id")] = requestId.RequestId.ToHexString();
                    return Task.CompletedTask;
                }, null);
                context.Features.Get<IWebResponseCompletionFeature>().ShouldNotBeNull().Register(() =>
                {
                    completionCalled = true;
                    return ValueTask.CompletedTask;
                });
                await context.Request.Body.CopyToAsync(context.Response.Body, token);
            }),
        };
        options.Listener.UseHttp1(transport);
        WebApplicationServer server = new(options);

        // Act
        try
        {
            await server.StartAsync(timeout.Token);
            string response = await SendAsync(transport,
                $"POST /direct HTTP/1.1\r\nHost: localhost\r\nContent-Length: 4\r\nConnection: close\r\ntraceparent: 00-{expectedId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01\r\n\r\nping",
                timeout.Token);
            await server.StopAsync(timeout.Token);

            // Assert
            response.ShouldContain("200 OK", Case.Sensitive);
            response.ShouldContain(expectedId.ToHexString(), Case.Sensitive);
            response.ShouldEndWith("ping");
            startingCalls.ShouldBe(1);
            completionCalled.ShouldBeTrue();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Direct server: A malformed request body publishes the transport's client fault")]
    public async Task StartAsync_MalformedRequestBody_ShouldPublishClientFaultWithoutHosting()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using InMemoryConnectionListener transport = new();
        HttpStatusCode? observed = null;
        WebApplicationServerOptions options = new()
        {
            Pipeline = new FakePipeline(async (context, token) =>
            {
                IWebClientFaultFeature fault = context.Features.Get<IWebClientFaultFeature>().ShouldNotBeNull();
                try
                {
                    await context.Request.Body.CopyToAsync(Stream.Null, token);
                }
                catch (InvalidDataException)
                {
                    observed = fault.StatusCode;
                    throw;
                }
            }),
        };
        options.Listener.UseHttp1(transport);
        WebApplicationServer server = new(options);

        // Act
        try
        {
            await server.StartAsync(timeout.Token);
            string response = await SendAsync(transport,
                "POST /fault HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\nZ\r\ninvalid\r\n",
                timeout.Token);
            await server.StopAsync(timeout.Token);

            // Assert
            response.ShouldContain("400 Bad Request", Case.Sensitive);
            observed.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Constructor: Own interceptors precede configured interceptors and capacity is rounded once")]
    public async Task Constructor_ConfiguredListener_ShouldInstallDefaultsWithoutMutatingReusableOptions()
    {
        // Arrange
        await using InMemoryConnectionListener transport = new();
        WebApplicationServerOptions options = new() { Pipeline = new FakePipeline() };
        ProbeInterceptor configured = new();
        options.Listener.Interceptors.Add(configured);
        options.Listener.ExchangeFeatureCapacity = 4;
        IHttpExchangeInterceptor[]? captured = null;
        int capacity = -1;
        options.Listener.UseHttp1(() =>
        {
            captured = options.Listener.Interceptors.ToArray();
            capacity = options.Listener.ExchangeFeatureCapacity;
            return transport;
        });

        // Act
        WebApplicationServer server = new(options);
        try
        {
            // Assert
            captured.ShouldNotBeNull();
            captured!.Length.ShouldBe(3);
            captured[0].ShouldBeOfType<WebClientFaultInterceptor>();
            captured[1].ShouldBeOfType<WebResponseStartingInterceptor>();
            captured[2].ShouldBeSameAs(configured);
            capacity.ShouldBe(17);
            options.Listener.Interceptors.ShouldHaveSingleItem().ShouldBeSameAs(configured);
            options.Listener.ExchangeFeatureCapacity.ShouldBe(4);
            options.TelemetryQueryValues.Add("Page").ShouldBeTrue();
            options.TelemetryQueryValues.Add("PAGE").ShouldBeFalse();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<string> SendAsync(InMemoryConnectionListener transport, string request, CancellationToken cancellationToken)
    {
        await using IDuplexConnection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = connection.AsStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private sealed class ProbeInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;
    }
}
