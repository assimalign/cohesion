using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Routing;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Tests.TestObjects;

internal sealed class CertificateTestServer : IAsyncDisposable
{
    private IWebApplicationServer _server = null!;
    private int _requests;

    private CertificateTestServer(int port)
    {
        Port = port;
    }

    public int Port { get; }
    public int Requests => Volatile.Read(ref _requests);
    public TaskCompletionSource<ClaimsPrincipal> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<CertificateTestServer> StartAsync(
        TlsServerOptions tls,
        CancellationToken cancellationToken,
        Action<CertificateAuthenticationOptions>? configure = null,
        bool alternateDefault = false,
        string? requiredRole = null)
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttps(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port), tls));
        builder.Services.AddRouting();
        builder.Services.AddAuthentication(authentication =>
        {
            authentication.AddCertificate(configure);
            authentication.Options.DefaultScheme = CertificateAuthenticationDefaults.AuthenticationScheme;
            if (alternateDefault)
            {
                authentication.Options.DefaultScheme = "Alternate";
                authentication.Options.AddScheme(new AuthenticationScheme("Alternate", null, () => new AlternateAuthenticationHandler()));
            }
        });
        builder.Services.AddAuthorization();
        WebApplication app = builder.Build();
        CertificateTestServer server = new(port);

        app.Use((context, next) =>
        {
            Interlocked.Increment(ref server._requests);
            return next(context);
        });
        IRouterBuilder routes = app.UseRouting();
        app.UseAuthentication();
        app.Use((context, next) =>
        {
            context.Response.Headers["X-Default-User"] = context.User.Identity?.Name ?? "anonymous";
            return next(context);
        });
        app.UseAuthorization();
        RouterRouteHandler endpoint = new(async context =>
        {
            server.Observed.TrySetResult(context.User);
            context.Response.StatusCode = HttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(context.User.Identity?.Name ?? "anonymous"), context.RequestCancelled);
        });
        routes.Map(HttpMethod.Get, "/public", endpoint);
        routes.Map(HttpMethod.Get, "/secured", endpoint).RequireAuthorization(policy =>
        {
            policy.AddAuthenticationSchemes(CertificateAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser();
            if (requiredRole is not null)
            {
                policy.RequireRole(requiredRole);
            }
        });

        server._server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server._server.StartAsync(cancellationToken).ConfigureAwait(false);
        return server;
    }

    public async ValueTask DisposeAsync() => await _server.StopAsync(CancellationToken.None).ConfigureAwait(false);
}

internal sealed class AlternateAuthenticationHandler : IAuthenticationHandler
{
    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;

    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        _scheme = scheme;
        _context = context;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "alternate"), new Claim(ClaimTypes.Role, "admin")], _scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), null, _scheme.Name)));
    }

    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        _context.Response.StatusCode = HttpStatusCode.Unauthorized;
        _context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = _scheme.Name;
        return Task.CompletedTask;
    }

    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        _context.Response.StatusCode = HttpStatusCode.Forbidden;
        return Task.CompletedTask;
    }
}
