using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.RateLimiting;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Antiforgery;
using Assimalign.Cohesion.Web.AotGuard;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Compression;
using Assimalign.Cohesion.Web.CookiePolicy;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.OpenApi;
using Assimalign.Cohesion.Web.RateLimiting;
using Assimalign.Cohesion.Web.RequestTimeouts;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.SecurityHeaders;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.StaticFiles;
using Assimalign.Cohesion.Web.Validation;

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
builder.AddAuthorization(options => options.AddPolicy("admins", policy => policy.RequireRole("admin")));
builder.AddAntiforgery();
builder.AddOpenApi(options => options.Title = "Cohesion Web AOT guard");
builder.AddValidation(validation => validation.AddProfile(new GuardItemProfile()));

await using WebApplication application = builder.Build();

// Outermost, ahead of the exception boundary, so error pages carry the headers too.
application.UseSecurityHeaders();
application.UseErrorHandling();
application.UseCookiePolicy();
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

// Endpoint policy middleware reads the endpoint UseRouting published, so it follows it. CORS goes
// first: it answers preflights before anything that could reject one.
application.UseCors(options => options
    .AddDefaultPolicy(policy => policy.WithOrigins(GuardSmoke.TrustedOrigin))
    .AddPolicy("json-clients", policy => policy.WithOrigins(GuardSmoke.TrustedOrigin).WithHeaders("Content-Type")));
application.UseAuthorization();
application.UseRequestTimeouts(TimeSpan.FromSeconds(30));
application.UseRateLimiting(options => options.GlobalPolicy = RateLimitingPolicy.Create(
    static (IHttpContext _) => "global",
    static (string _) => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 1000,
        Window = TimeSpan.FromMinutes(1),
    })));
application.UseAntiforgery();

application.MapGet("/items/{id:int}", async (int id, IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.WriteContentAsync(new GuardItem(id, $"item-{id}"), context.RequestCancelled);
});

application.MapPost("/items", async (GuardItem item, IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.WriteContentAsync(item with { Name = item.Name + "-echo" }, context.RequestCancelled);
})
    .RequireCors("json-clients");

// Handler return values (#1059): the generated thunk writes the value. A model is negotiated through the
// source-generated JSON context, a string is text/plain, and null answers 204.
application.MapGet("/values/{id:int}", (int id) => new GuardItem(id, $"value-{id}"));
application.MapGet("/values/async/{id:int}", async (int id) =>
{
    await Task.Yield();
    return new GuardItem(id, $"async-value-{id}");
});
application.MapGet("/greeting", () => "hello from the AOT guard");
application.MapGet("/values/none", () => (GuardItem?)null);

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

// A named role policy: UseAuthorization challenges, forbids or admits before the endpoint runs.
application.MapGet("/admin", async (IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(context.User.Identity?.Name ?? string.Empty), context.RequestCancelled);
}).RequireAuthorization("admins");

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

// Antiforgery: the render path mints the token pair; the protected post requires it.
application.MapGet("/antiforgery/token", async (IHttpContext context) =>
{
    HttpAntiforgeryTokenSet tokens = context.RequireAntiforgery.GetAndStoreTokens(context);
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(tokens.RequestToken ?? string.Empty), context.RequestCancelled);
});
application.Map(HttpMethod.Post, "/antiforgery/submit", async (IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync("accepted"u8.ToArray(), context.RequestCancelled);
}).RequireAntiforgery();

// The cookie policy judges cookies as they are appended: SameSite=None without Secure is upgraded.
application.MapGet("/cookies", (IHttpContext context) =>
{
    context.Response.Cookies.Add(new HttpCookie("guard-tracking", "1", new HttpCookieOptions { SameSite = HttpCookieSameSiteMode.None }));
    context.Response.StatusCode = HttpStatusCode.NoContent;
    return Task.CompletedTask;
});

// File binding (#1061): an uploaded file binds to a typed handler under Http.Forms' limits. Form-bound
// endpoints require antiforgery by default; this one opts out, since antiforgery has its own checks.
application.MapPost("/upload", async (IHttpFormFile file, IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{file.FileName}:{file.Length}"), context.RequestCancelled);
}).DisableAntiforgery();

// The OpenAPI document (#152): built once from the typed endpoints' metadata and the source-generated
// JSON contracts, then served with an ETag.
application.MapOpenApi();

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
