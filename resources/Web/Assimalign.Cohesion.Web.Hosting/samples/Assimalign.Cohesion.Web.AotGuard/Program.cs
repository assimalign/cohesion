using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.RateLimiting;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.AotGuard;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;
using Assimalign.Cohesion.Web.Compression;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.RateLimiting;
using Assimalign.Cohesion.Web.RequestTimeouts;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.StaticFiles;

// NativeAOT guard for the Web area (#1052): a representative application composed the way a
// customer's Program.cs composes one. Run plainly it serves like any Web application; run with
// --smoke it serves on a free loopback port, exercises every feature over real HTTP, and exits
// non-zero on the first failed check.
bool smoke = Array.IndexOf(args, "--smoke") >= 0;
byte[] signingKey = RandomNumberGenerator.GetBytes(32);
int port = smoke ? GuardSmoke.GetFreeTcpPort() : 0;
string[] hostArgs = smoke
    ? ["--Http:Endpoints:Main:Host=127.0.0.1", $"--Http:Endpoints:Main:Port={port}"]
    : args;

WebApplicationBuilder builder = WebApplication.CreateBuilder(hostArgs);
builder.AddRouting();
builder.AddJsonSerialization(GuardJsonContext.Default);
builder.AddErrorHandling();
builder.AddAuthentication(options => options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme)
    .AddCookie()
    .AddJwtBearer(options =>
    {
        options.SigningKeys.Add(JwtSignatureVerifier.CreateHmac(signingKey));
        options.ValidIssuers.Add(GuardSmoke.Issuer);
        options.ValidAudiences.Add(GuardSmoke.Audience);
    });

await using WebApplication application = builder.Build();

application.UseErrorHandling();
application.UseResponseCompression();
application.UseRequestDecompression();
application.UseStaticFiles();
application.UseAuthentication();
application.Map("/branch", branch => branch.Run(async context =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
    await context.Response.Body.WriteAsync(
        Encoding.UTF8.GetBytes($"{context.GetPathBase()}|{context.GetEffectivePath()}"),
        context.RequestCancelled);
}));
application.UseRouting();

// Endpoint policy middleware reads the endpoint UseRouting published, so it follows it.
application.UseRequestTimeouts(TimeSpan.FromSeconds(30));
application.UseRateLimiting(options => options.GlobalPolicy = RateLimitingPolicy.Create(
    static (IHttpContext _) => "global",
    static (string _) => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 1000,
        Window = TimeSpan.FromMinutes(1),
    })));

application.MapGet("/items/{id:int}", async (int id, IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.WriteContentAsync(new GuardItem(id, $"item-{id}"), context.RequestCancelled);
});

application.MapPost("/items", async (GuardItem item, IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.WriteContentAsync(item with { Name = item.Name + "-echo" }, context.RequestCancelled);
});

application.MapGet("/large", async (IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
    await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(new string('c', 8192)), context.RequestCancelled);
});

application.MapGet("/me", async (IHttpContext context) =>
{
    if (context.User.Identity is not { IsAuthenticated: true } identity)
    {
        await context.ChallengeAsync(cancellationToken: context.RequestCancelled);
        return;
    }

    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(identity.Name ?? string.Empty), context.RequestCancelled);
});

application.MapGet("/boom", async (IHttpContext context) =>
{
    await Task.Yield();
    throw new InvalidOperationException("The AOT guard's fault endpoint.");
});

// A route group: the typed endpoint binds the group's prefix value, and the group's and the route's
// policy verbs must both be applied, or dispatch fails closed.
IRouterGroupBuilder tenants = application.MapGroup("/tenants/{tenant}")
    .WithRequestTimeout(TimeSpan.FromSeconds(10));
tenants.MapGet("orders/{id:int}", async (string tenant, int id, IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.WriteContentAsync(new GuardItem(id, $"{tenant}-order-{id}"), context.RequestCancelled);
})
    .WithName("tenant-order")
    .RequireRateLimiting(RateLimitingPolicy.Create(
        static (IHttpContext _) => "tenant-orders",
        static (string _) => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1000,
            Window = TimeSpan.FromMinutes(1),
        })));

// A single-page application's client routes; a path that names a file is never answered with it.
application.MapFallbackToFile("index.html");

if (!smoke)
{
    await application.RunAsync();
    return 0;
}

IWebApplication web = application;
using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(2));
await web.StartAsync(cancellation.Token);
try
{
    return await GuardSmoke.RunAsync(port, signingKey, cancellation.Token);
}
finally
{
    await web.StopAsync(CancellationToken.None);
}
