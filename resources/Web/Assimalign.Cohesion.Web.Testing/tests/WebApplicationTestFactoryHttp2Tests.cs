using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Testing.Tests;

/// <summary>
/// Prior-knowledge HTTP/2 (h2c) coverage for <see cref="WebApplicationTestFactory"/>: the
/// factory registers the in-memory listener through <c>UseHttp2</c> and its clients pin an
/// exact 2.0 version policy, so the exchange is HTTP/2 from the first byte over the plaintext
/// in-memory duplex pair — no TLS, no ALPN, no Upgrade dance. HTTP/3 is out of scope for the
/// factory (QUIC-bound; see docs/DESIGN.md).
/// </summary>
public class WebApplicationTestFactoryHttp2Tests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - Http2: Should serve a prior-knowledge HTTP/2 request over the in-memory pair")]
    public async Task CreateClient_Http2Factory_ShouldServePriorKnowledgeHttp2()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });

        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;

            byte[] payload = Encoding.UTF8.GetBytes("served over h2");
            await context.Response.Body.WriteAsync(payload, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/h2", cancellationToken);

        // Assert — the negotiated client version proves prior-knowledge h2 end to end.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Version.ShouldBe(NetHttpVersion.Version20);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("served over h2");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - Http2: Concurrent requests should multiplex streams over one in-memory connection")]
    public async Task CreateClient_Http2ConcurrentRequests_ShouldMultiplexOverOneConnection()
    {
        // Arrange — the in-memory driver mints a distinct ephemeral endpoint per dialed
        // connection, so a single observed remote endpoint across concurrent exchanges proves
        // the client multiplexed them as streams of one HTTP/2 connection.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });

        ConcurrentQueue<string?> observedConnections = new();
        factory.Application.Use((context, next) =>
        {
            observedConnections.Enqueue(context.ConnectionInfo.RemoteEndPoint?.ToString());
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        Task<HttpResponseMessage> first = client.GetAsync("/one", cancellationToken);
        Task<HttpResponseMessage> second = client.GetAsync("/two", cancellationToken);

        HttpResponseMessage[] responses = await Task.WhenAll(first, second);

        // Assert
        foreach (HttpResponseMessage response in responses)
        {
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            response.Version.ShouldBe(NetHttpVersion.Version20);
            response.Dispose();
        }

        observedConnections.Count.ShouldBe(2);
        observedConnections.Distinct().Count().ShouldBe(1);
    }
}
