using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;
using Assimalign.Cohesion.Web.Authentication.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authentication.Tests;

/// <summary>
/// Covers the builder-time composition chain now homed with the scheme model:
/// <c>AddAuthentication</c> on <see cref="IWebApplicationBuilder"/>, the handler packages'
/// grafted <c>AddCookie</c>/<c>AddJwtBearer</c> verbs, and the <c>UseAuthentication</c>
/// middleware. Moved from the Web.Hosting tests when the verbs left the hosting module
/// (Web-area dependency rule: hosting neither references nor is referenced by feature libraries).
/// </summary>
public sealed class AuthenticationCompositionTests : IDisposable
{
    private readonly string _keysDirectory;
    private readonly IDataProtectionProvider _dataProtection;

    public AuthenticationCompositionTests()
    {
        _keysDirectory = Path.Combine(Path.GetTempPath(), "cohesion-web-auth-tests", Guid.NewGuid().ToString("N"));
        _dataProtection = DataProtectionProvider.Create(KeyRepository.CreateFileSystem(_keysDirectory));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AddAuthentication registers cookie and bearer schemes")]
    public void AddAuthentication_RegistersCookieAndBearerSchemes()
    {
        // Arrange
        StubWebApplicationBuilder builder = new();

        // Act
        AuthenticationBuilder auth = builder
            .AddAuthentication(options => options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme, _dataProtection)
            .AddCookie()
            .AddJwtBearer(options => options.SigningKeys.Add(
                JwtSignatureVerifier.CreateHmac(Encoding.UTF8.GetBytes("a-256-bit-hmac-signing-key-for-tests!!!!"))));

        // Assert
        auth.Options.GetScheme(CookieAuthenticationDefaults.AuthenticationScheme).ShouldNotBeNull();
        auth.Options.GetScheme(JwtBearerDefaults.AuthenticationScheme).ShouldNotBeNull();
        auth.Options.ResolveDefaultChallengeScheme().ShouldBe(CookieAuthenticationDefaults.AuthenticationScheme);
        builder.Features.ShouldContain(feature => feature is IAuthenticationService);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AddCookie wires a working ticket protector from the provider")]
    public async Task AddCookie_WiresTicketProtector()
    {
        // Arrange
        StubWebApplicationBuilder builder = new();
        AuthenticationBuilder auth = builder
            .AddAuthentication(options => options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme, _dataProtection)
            .AddCookie();

        AuthenticationScheme scheme = auth.Options.GetScheme(CookieAuthenticationDefaults.AuthenticationScheme)!;
        IAuthenticationHandler handler = scheme.CreateHandler();
        handler.ShouldBeAssignableTo<IAuthenticationSignInHandler>();

        TestHttpContext context = TestHttpContext.Create();
        await handler.InitializeAsync(scheme, context);

        // Act — a sign-in must succeed (would throw if TicketProtector had not been wired).
        await ((IAuthenticationSignInHandler)handler).SignInAsync(
            new ClaimsPrincipal(new ClaimsIdentity("Cookies")), properties: null);

        // Assert — a Set-Cookie was emitted.
        context.Response.Headers.ContainsKey(HttpHeaderKey.SetCookie).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - UseAuthentication populates context.User from the default scheme")]
    public async Task UseAuthentication_PopulatesUser()
    {
        // Arrange
        AuthenticationOptions options = new() { DefaultScheme = "Test" };
        ClaimsPrincipal principal = new(new ClaimsIdentity("Test"));
        options.AddScheme(new AuthenticationScheme("Test", null, () => new StubHandler(
            AuthenticateResult.Success(new AuthenticationTicket(principal, null, "Test")))));

        TestHttpContext context = TestHttpContext.Create();
        context.Features.Set<IAuthenticationService>(AuthenticationService.Create(options));

        RecordingPipelineBuilder pipeline = new();
        pipeline.UseAuthentication();

        // Act
        await pipeline.InvokeAsync(context);

        // Assert
        pipeline.ReachedTerminal.ShouldBeTrue();
        context.User.ShouldBeSameAs(principal);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - UseAuthentication is a pass-through with no default authenticate scheme")]
    public async Task UseAuthentication_NoDefaultScheme_PassesThrough()
    {
        // Arrange — a service with no default authenticate scheme.
        AuthenticationOptions options = new();
        TestHttpContext context = TestHttpContext.Create();
        context.Features.Set<IAuthenticationService>(AuthenticationService.Create(options));

        RecordingPipelineBuilder pipeline = new();
        pipeline.UseAuthentication();

        // Act
        await pipeline.InvokeAsync(context);

        // Assert — the pipeline continues and no principal is installed.
        pipeline.ReachedTerminal.ShouldBeTrue();
        context.User.Identity!.IsAuthenticated.ShouldBeFalse();
    }

    private sealed class StubHandler : IAuthenticationHandler
    {
        private readonly AuthenticateResult _result;

        public StubHandler(AuthenticateResult result) => _result = result;

        public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_result);

        public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// A minimal <see cref="IWebApplicationBuilder"/> that records registered features — the only
    /// builder capability <c>AddAuthentication</c> composes against.
    /// </summary>
    private sealed class StubWebApplicationBuilder : IWebApplicationBuilder
    {
        public List<IHttpFeature> Features { get; } = new();

        public IWebApplicationContext Context => throw new NotSupportedException();

        public IWebApplicationBuilder AddFeature(IHttpFeature feature)
        {
            Features.Add(feature);
            return this;
        }

        public IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure) => this;

        public IWebApplicationBuilder AddServer(IWebApplicationServer server) => this;

        public IWebApplicationBuilder AddServer(Func<IWebApplicationContext, IWebApplicationServer> server) => this;

        public IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline) => this;

        public IWebApplication Build() => throw new NotSupportedException();
    }

    /// <summary>
    /// Captures the single middleware registered by <c>UseAuthentication</c> and runs it against a
    /// terminal no-op so the middleware's effect on the context can be observed.
    /// </summary>
    private sealed class RecordingPipelineBuilder : IWebApplicationPipelineBuilder
    {
        private Func<WebApplicationMiddleware, WebApplicationMiddleware>? _component;

        public bool ReachedTerminal { get; private set; }

        public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware) => this;

        public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        {
            _component = middleware;
            return this;
        }

        public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware) => this;

        public IWebApplicationPipeline Build() => throw new NotSupportedException();

        public Task InvokeAsync(IHttpContext context)
        {
            WebApplicationMiddleware terminal = _ =>
            {
                ReachedTerminal = true;
                return Task.CompletedTask;
            };

            WebApplicationMiddleware chain = _component!(terminal);
            return chain(context);
        }
    }
}
