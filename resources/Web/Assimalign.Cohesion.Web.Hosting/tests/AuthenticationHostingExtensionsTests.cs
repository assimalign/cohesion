using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class AuthenticationHostingExtensionsTests : IDisposable
{
    private readonly string _keysDirectory;
    private readonly IDataProtectionProvider _dataProtection;

    public AuthenticationHostingExtensionsTests()
    {
        _keysDirectory = Path.Combine(Path.GetTempPath(), "cohesion-hosting-auth-tests", Guid.NewGuid().ToString("N"));
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - AddAuthentication registers cookie and bearer schemes")]
    public void AddAuthentication_RegistersCookieAndBearerSchemes()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

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
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - AddCookie wires a working ticket protector from the provider")]
    public async Task AddCookie_WiresTicketProtector()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseAuthentication populates context.User from the default scheme")]
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseAuthentication is a pass-through with no default authenticate scheme")]
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

    private sealed class TestHttpRequest : HttpRequest
    {
        private HttpContext? _httpContext;

        public override HttpHost Host { get; set; } = HttpHost.Empty;
        public override HttpPath Path { get; set; } = HttpPath.Root;
        public override HttpMethod Method { get; set; } = HttpMethod.Get;
        public override HttpScheme Scheme { get; set; } = HttpScheme.Http;
        public override HttpQueryCollection Query { get; } = new HttpQueryCollection();
        public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public override Stream Body { get; set; } = Stream.Null;

        public override HttpContext HttpContext => _httpContext
            ?? throw new InvalidOperationException("Context not attached.");

        internal void AttachContext(HttpContext context) => _httpContext ??= context;
    }

    private sealed class TestHttpResponse : HttpResponse
    {
        private HttpContext? _httpContext;

        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public override Stream Body { get; set; } = new MemoryStream();

        public override HttpContext HttpContext => _httpContext
            ?? throw new InvalidOperationException("Context not attached.");

        internal void AttachContext(HttpContext context) => _httpContext ??= context;
    }

    private sealed class TestHttpContext : HttpContext
    {
        private TestHttpContext(TestHttpRequest request, TestHttpResponse response)
        {
            Version = HttpVersion.Http11;
            Request = request;
            Response = response;
            ConnectionInfo = HttpConnectionInfo.Empty;
            Features = new HttpFeatureCollection();
            Items = new Dictionary<string, object?>(StringComparer.Ordinal);
            RequestCancelled = CancellationToken.None;

            request.AttachContext(this);
            response.AttachContext(this);
        }

        public override HttpVersion Version { get; }
        public override TestHttpRequest Request { get; }
        public override TestHttpResponse Response { get; }
        public override HttpConnectionInfo ConnectionInfo { get; }
        public override HttpFeatureCollection Features { get; }
        public override IDictionary<string, object?> Items { get; }
        public override CancellationToken RequestCancelled { get; }

        public override void Cancel() { }
        public override Task CancelAsync() => Task.CompletedTask;
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public static TestHttpContext Create() => new(new TestHttpRequest(), new TestHttpResponse());
    }
}
