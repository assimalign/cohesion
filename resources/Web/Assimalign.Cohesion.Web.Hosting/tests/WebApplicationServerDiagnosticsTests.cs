using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The default server reports its failures through its event source with server and connection
/// identities, deliberate levels, and no request or response content.
/// </summary>
[Collection(nameof(TelemetryCollection))]
public class WebApplicationServerDiagnosticsTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Diagnostics: A real response fault reports no header values, bodies or target")]
    public async Task ServeConnection_WhenARealResponseFails_ShouldNotReportHeaderValuesOrBodies()
    {
        // Arrange — a real response body fails while the HTTP/1.1 transport frames it.
        using CancellationTokenSource cancellation = new(_timeout);
        using EventSourceRecorder recorded = new(await ServerDiagnosticsSource.GetAsync(), EventLevel.Verbose);
        await using WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Body = new FailingReadStream();
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        long serverId = (long)Payload(recorded.Events.Single(entry => entry.EventId == 1), "serverId")!;
        using HttpRequestMessage request = new(HttpMethod.Post, "/secret")
        {
            Content = new StringContent("request-body-secret", Encoding.UTF8),
        };
        request.Headers.Add("X-Api-Key", "header-value-secret");

        // Act
        await Should.ThrowAsync<Exception>(() => client.SendAsync(request, cancellation.Token));
        EventWrittenEventArgs entry = await WaitForEventAsync(recorded, serverId, 4);

        // Assert
        entry.Level.ShouldBe(EventLevel.Error);
        Payload(entry, "connectionId").ShouldBeOfType<string>().ShouldNotBeNullOrEmpty();
        Payload(entry, "protocolVersion").ShouldBe("1.1");
        string reported = string.Join(" ", entry.PayloadNames!.Zip(entry.Payload!, (name, value) => $"{name}={value}"));
        reported.ShouldNotContain("header-value-secret", Case.Sensitive);
        reported.ShouldNotContain("request-body-secret", Case.Sensitive);
        reported.ShouldNotContain("/secret", Case.Sensitive);
        recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Diagnostics: The default server reports a real bind collision through its event source")]
    public async Task Build_WhenASecondServerCannotBind_ShouldReportItsDistinctServerIdentity()
    {
        // Arrange — a first application holds a loopback port; a second tries to bind it.
        using EventSourceRecorder recorded = new(await ServerDiagnosticsSource.GetAsync(), EventLevel.Verbose);
        TcpConnectionListener holder = new(new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
        WebApplicationBuilder holderBuilder = WebApplication.CreateBuilder();
        holderBuilder.Server.UseServer(options => options.UseHttp1(holder));
        await using WebApplication holderApplication = holderBuilder.Build();
        await ((IWebApplication)holderApplication).StartAsync(CancellationToken.None);
        long holderServerId = (long)Payload(recorded.Events.Single(entry => entry.EventId == 1), "serverId")!;

        TcpConnectionListener competitor = new(new TcpConnectionListenerOptions { EndPoint = (IPEndPoint)holder.EndPoint });
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(competitor));
        await using WebApplication application = builder.Build();

        // Act
        try
        {
            await Should.ThrowAsync<HostStartupException>(() => ((IWebApplication)application).StartAsync(CancellationToken.None));

            // Assert
            EventWrittenEventArgs entry = recorded.Events.Where(candidate => candidate.EventId == 2).ShouldHaveSingleItem();
            long competitorServerId = (long)Payload(entry, "serverId")!;
            competitorServerId.ShouldNotBe(holderServerId);
            ServerEvents(recorded, holderServerId, 1).ShouldHaveSingleItem();
            ServerEvents(recorded, holderServerId, 2).ShouldBeEmpty();
            entry.Level.ShouldBe(EventLevel.Critical);
            Payload(entry, "exceptionType").ShouldBeOfType<string>().ShouldNotBeNullOrEmpty();
            recorded.Events.ShouldNotContain(candidate => candidate.EventId == 0);
        }
        finally
        {
            await ((IWebApplication)holderApplication).StopAsync(CancellationToken.None);
        }
    }

    private static EventWrittenEventArgs[] ServerEvents(EventSourceRecorder recorded, long serverId, int? eventId = null)
        => recorded.Events.Where(entry =>
            (!eventId.HasValue || entry.EventId == eventId.Value) &&
            entry.PayloadNames is { Count: > 0 } && entry.PayloadNames[0] == "serverId" &&
            entry.Payload is { Count: > 0 } && Equals(entry.Payload[0], serverId)).ToArray();

    private static object? Payload(EventWrittenEventArgs entry, string name)
        => entry.Payload![entry.PayloadNames!.IndexOf(name)];

    private static async Task<EventWrittenEventArgs> WaitForEventAsync(EventSourceRecorder recorded, long serverId, int eventId)
    {
        using CancellationTokenSource timeout = new(_timeout);

        while (true)
        {
            EventWrittenEventArgs? entry = ServerEvents(recorded, serverId, eventId).FirstOrDefault();

            if (entry is not null)
            {
                return entry;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

}
