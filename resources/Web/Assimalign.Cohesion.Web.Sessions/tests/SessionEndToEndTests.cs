using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/>
/// (in-memory HTTP/1.1): a session round-trips across two requests on one client
/// through the session-id cookie, and a request that never touches the session
/// establishes nothing. Requests are sequential on one client (safe for the
/// sequential in-memory dispatch).
/// </summary>
public class SessionEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - E2E: A session should round-trip across requests via the session cookie")]
    public async Task UseSessions_AcrossTwoRequests_ShouldPersistStateViaCookie()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseSessions();
        factory.Application.Use(async (context, next) =>
        {
            IHttpSession session = await context.LoadSessionAsync(context.RequestCancelled);
            int hits = session.GetInt32("hits") ?? 0;
            hits++;
            session.SetInt32("hits", hits);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(hits.ToString()), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act — same client, so the handler's CookieContainer carries the session cookie forward
        string first = await (await client.GetAsync("/", cancellationToken)).Content.ReadAsStringAsync(cancellationToken);
        string second = await (await client.GetAsync("/", cancellationToken)).Content.ReadAsStringAsync(cancellationToken);
        string third = await (await client.GetAsync("/", cancellationToken)).Content.ReadAsStringAsync(cancellationToken);

        // Assert — the counter advances only if prior state was reloaded from the store
        first.ShouldBe("1");
        second.ShouldBe("2");
        third.ShouldBe("3");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - E2E: A request that never touches the session sets no cookie")]
    public async Task UseSessions_SessionUntouched_ShouldNotSetCookie()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseSessions();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellationToken);

        // Assert — no session was established, so no Set-Cookie was emitted
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - E2E: A fresh client (no cookie) starts an independent session")]
    public async Task UseSessions_FreshClient_ShouldStartIndependentSession()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseSessions();
        factory.Application.Use(async (context, next) =>
        {
            IHttpSession session = await context.LoadSessionAsync(context.RequestCancelled);
            int hits = session.GetInt32("hits") ?? 0;
            hits++;
            session.SetInt32("hits", hits);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(hits.ToString()), context.RequestCancelled);
        });

        // Act — first client advances the counter, a second (cookie-less) client starts over
        using HttpClient first = factory.CreateClient();
        (await (await first.GetAsync("/", cancellationToken)).Content.ReadAsStringAsync(cancellationToken)).ShouldBe("1");
        (await (await first.GetAsync("/", cancellationToken)).Content.ReadAsStringAsync(cancellationToken)).ShouldBe("2");

        using HttpClient second = factory.CreateClient();
        string secondClientFirstHit = await (await second.GetAsync("/", cancellationToken)).Content.ReadAsStringAsync(cancellationToken);

        // Assert
        secondClientFirstHit.ShouldBe("1");
    }
}
