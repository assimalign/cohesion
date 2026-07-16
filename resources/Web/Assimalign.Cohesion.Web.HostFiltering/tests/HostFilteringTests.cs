using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.HostFiltering.Tests;

/// <summary>
/// End-to-end coverage for allowed-hosts enforcement (issue #781): the
/// <c>UseHostFiltering</c> verb compiles the allowlist once at registration and its
/// middleware — registered first, per the package's ordering contract — rejects mismatching
/// hosts ahead of everything registered after it. HttpClient-driven cases ride
/// <see cref="WebApplicationTestFactory"/> (origin-form HTTP/1.1 and HTTP/2 <c>:authority</c>);
/// the missing-Host and absolute-form cases speak raw HTTP/1.1 over an in-memory connection,
/// because <see cref="HttpClient"/> always emits a <c>Host</c> header and only ever sends
/// origin-form targets.
/// </summary>
public class HostFilteringTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: Without UseHostFiltering any host should be accepted (opt-in)")]
    public async Task HostFiltering_NotRegistered_ShouldAcceptAnyHost()
    {
        // Arrange — the default: the verb is never called, no middleware is installed.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("http://any.host.example/", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: An exact allowlist should pass matching hosts and reject others with an empty 400")]
    public async Task HostFiltering_ExactAllowlist_ShouldPassMatchAndRejectOthers()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("allowed.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage allowed = await client.GetAsync("http://allowed.example/", cancellationToken);
        using HttpResponseMessage denied = await client.GetAsync("http://denied.example/", cancellationToken);
        using HttpResponseMessage allowedAgain = await client.GetAsync("http://allowed.example/again", cancellationToken);

        // Assert — the rejection is a clean 400 with an empty body, and it does not tear down
        // the connection: a subsequent allowed request on the same client still succeeds.
        allowed.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        denied.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        (await denied.Content.ReadAsStringAsync(cancellationToken)).ShouldBeEmpty();
        allowedAgain.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: Registered first, a rejection should short-circuit everything registered after it")]
    public async Task HostFiltering_RegisteredFirst_ShouldShortCircuitLaterMiddleware()
    {
        // Arrange — the package's ordering contract: UseHostFiltering goes first, so no later
        // middleware ever observes a request whose host failed validation.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("allowed.example"));

        bool laterMiddlewareRan = false;
        factory.Application.Use((context, next) =>
        {
            laterMiddlewareRan = true;
            return next.Invoke(context);
        });
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage denied = await client.GetAsync("http://denied.example/", cancellationToken);

        // Assert
        denied.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        laterMiddlewareRan.ShouldBeFalse();

        // The same pipeline serves an allowed host normally.
        using HttpResponseMessage allowed = await client.GetAsync("http://allowed.example/", cancellationToken);
        allowed.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        laterMiddlewareRan.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: Host matching should be case-insensitive")]
    public async Task HostFiltering_UppercaseHostHeader_ShouldMatchCaseInsensitively()
    {
        // Arrange — the Uri class lowercases URI hosts, so the uppercase form is forced
        // through the Host request header instead.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("allowed.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/");
        request.Headers.Host = "ALLOWED.EXAMPLE";

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: The request's port should be ignored by a portless pattern")]
    public async Task HostFiltering_HostWithPort_ShouldMatchPortlessPattern()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("allowed.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act — the explicit non-default port rides the Host header ("allowed.example:8080").
        using HttpResponseMessage response = await client.GetAsync("http://allowed.example:8080/", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: A wildcard pattern should match subdomain depth and exclude the apex and lookalikes")]
    public async Task HostFiltering_WildcardPattern_ShouldMatchSubdomainsOnly()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("*.example.com"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage subdomain = await client.GetAsync("http://api.example.com/", cancellationToken);
        using HttpResponseMessage deep = await client.GetAsync("http://a.b.example.com/", cancellationToken);
        using HttpResponseMessage apex = await client.GetAsync("http://example.com/", cancellationToken);
        using HttpResponseMessage lookalike = await client.GetAsync("http://evilexample.com/", cancellationToken);

        // Assert
        subdomain.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        deep.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        apex.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        lookalike.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: An IPv6 literal pattern should match a bracketed request host")]
    public async Task HostFiltering_Ipv6Pattern_ShouldMatchBracketedRequestHost()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("[::1]"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act — the client sends "Host: [::1]"; matching is bracket-insensitive.
        using HttpResponseMessage response = await client.GetAsync("http://[::1]/", cancellationToken);
        using HttpResponseMessage denied = await client.GetAsync("http://other.example/", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        denied.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: The * pattern should accept every host while keeping the middleware installed")]
    public async Task HostFiltering_MatchAnyPattern_ShouldAcceptEveryHost()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("*"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("http://one.example/", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("http://two.example:9090/", cancellationToken);

        // Assert
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: An invalid allowlist pattern should fail at registration, not at request time")]
    public void HostFiltering_InvalidPattern_ShouldFailAtRegistration()
    {
        // Arrange — a port-bearing pattern is a configuration error: it would otherwise never
        // match (filtering ignores ports) and must fail loudly when the matcher compiles,
        // which happens inside the UseHostFiltering call.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        WebApplication application = builder.Build();

        // Act & Assert
        Should.Throw<ArgumentException>(
            () => application.UseHostFiltering(options => options.AllowedHosts.Add("allowed.example:8080")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: An empty allowlist should fail at registration rather than deny every request")]
    public void HostFiltering_EmptyAllowlist_ShouldFailAtRegistration()
    {
        // Arrange — calling the verb opts in to filtering; an empty allowlist would compile
        // to deny-all, so it is rejected as a configuration error.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        WebApplication application = builder.Build();

        // Act & Assert
        Should.Throw<ArgumentException>(() => application.UseHostFiltering(_ => { }));
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: HTTP/2 requests should be validated through the resolved :authority")]
    public async Task HostFiltering_Http2Authority_ShouldValidateResolvedAuthority()
    {
        // Arrange — prior-knowledge HTTP/2 over the in-memory transport: the effective host is
        // the ':authority' pseudo-header, resolved by the transport before dispatch.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("allowed.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage allowed = await client.GetAsync("http://allowed.example/", cancellationToken);
        using HttpResponseMessage denied = await client.GetAsync("http://denied.example/", cancellationToken);

        // Assert
        allowed.Version.ShouldBe(System.Net.HttpVersion.Version20);
        allowed.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        denied.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: An HTTP/1.1 request without a Host header should be rejected with 400 by default")]
    public async Task HostFiltering_MissingHostHeader_ShouldRejectWith400()
    {
        // Arrange — RFC 9112 §3.2: the request cannot be validated against the allowlist, and
        // AllowEmptyHost defaults to false. HttpClient always sends Host, so this speaks raw
        // HTTP/1.1 over the in-memory transport.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        (WebApplication application, IWebApplicationServer server, InMemoryConnectionListener listener) =
            await StartRawHttp1ApplicationAsync(
                options => options.AllowedHosts.Add("allowed.example"),
                cancellationToken);

        try
        {
            // Act
            string response = await ExchangeRawHttp1Async(listener, "GET / HTTP/1.1\r\n\r\n", cancellationToken);

            // Assert
            response.ShouldStartWith("HTTP/1.1 400");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)application).DisposeAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: AllowEmptyHost should let a hostless HTTP/1.1 request through")]
    public async Task HostFiltering_MissingHostHeader_WithAllowEmptyHost_ShouldPass()
    {
        // Arrange — the explicit opt-out for legacy HTTP/1.0-style clients.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        (WebApplication application, IWebApplicationServer server, InMemoryConnectionListener listener) =
            await StartRawHttp1ApplicationAsync(
                options =>
                {
                    options.AllowedHosts.Add("allowed.example");
                    options.AllowEmptyHost = true;
                },
                cancellationToken);

        try
        {
            // Act
            string response = await ExchangeRawHttp1Async(listener, "GET / HTTP/1.1\r\n\r\n", cancellationToken);

            // Assert
            response.ShouldStartWith("HTTP/1.1 200");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)application).DisposeAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - HostFiltering: An absolute-form target's authority should supersede the Host header (RFC 9112 §3.2.2)")]
    public async Task HostFiltering_AbsoluteFormTarget_ShouldSupersedeHostHeader()
    {
        // Arrange — the middleware validates the transport-resolved effective host, and for an
        // absolute-form request-target that is the target's authority, not the Host header.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        (WebApplication application, IWebApplicationServer server, InMemoryConnectionListener listener) =
            await StartRawHttp1ApplicationAsync(
                options => options.AllowedHosts.Add("allowed.example"),
                cancellationToken);

        try
        {
            // Act — a denied target authority is rejected even though the Host header is
            // allowlisted; an allowed target authority passes even though the Host header is
            // not (the server MUST ignore Host on an absolute-form request).
            string deniedTarget = await ExchangeRawHttp1Async(
                listener,
                "GET http://denied.example/ HTTP/1.1\r\nHost: allowed.example\r\n\r\n",
                cancellationToken);

            string allowedTarget = await ExchangeRawHttp1Async(
                listener,
                "GET http://allowed.example/ HTTP/1.1\r\nHost: denied.example\r\n\r\n",
                cancellationToken);

            // Assert
            deniedTarget.ShouldStartWith("HTTP/1.1 400");
            allowedTarget.ShouldStartWith("HTTP/1.1 200");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)application).DisposeAsync();
        }
    }

    /// <summary>
    /// Registers the terminal handler: every request that reaches the application pipeline
    /// answers 200 with an empty body.
    /// </summary>
    private static void UseTerminalOkHandler(WebApplication application)
    {
        application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Builds and starts a minimal application serving HTTP/1.1 on a private in-memory
    /// listener — host filtering registered first, then a terminal 200 handler — for tests
    /// that must write raw request bytes.
    /// </summary>
    private static async Task<(WebApplication Application, IWebApplicationServer Server, InMemoryConnectionListener Listener)> StartRawHttp1ApplicationAsync(
        Action<HostFilteringOptions> configureFiltering,
        CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        InMemoryConnectionListener listener = new();

        builder.Server.UseServer(options => options.UseHttp1(listener));

        WebApplication application = builder.Build();
        application.UseHostFiltering(configureFiltering);
        UseTerminalOkHandler(application);

        IWebApplicationServer server = application.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellationToken);

        return (application, server, listener);
    }

    /// <summary>
    /// Dials the in-memory listener, writes one raw HTTP/1.1 request, and reads until the end
    /// of the response head (the blank line), returning everything read as ASCII text.
    /// </summary>
    private static async Task<string> ExchangeRawHttp1Async(
        InMemoryConnectionListener listener,
        string request,
        CancellationToken cancellationToken)
    {
        InMemoryConnectionFactory connectionFactory = listener.CreateFactory();
        Connection connection = await connectionFactory.ConnectAsync(listener.EndPoint, cancellationToken);

        await using (connection.ConfigureAwait(false))
        {
            Stream stream = connection.AsStream();

            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            byte[] buffer = new byte[4096];
            StringBuilder response = new();

            while (!response.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                response.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return response.ToString();
        }
    }
}
