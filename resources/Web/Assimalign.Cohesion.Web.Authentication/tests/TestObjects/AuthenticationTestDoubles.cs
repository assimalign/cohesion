using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication.Tests.TestObjects;

internal sealed class TestHttpRequest : HttpRequest
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
        ?? throw new InvalidOperationException("The HttpContext back-reference has not been attached.");

    internal void AttachContext(HttpContext context) => _httpContext ??= context;
}

internal sealed class TestHttpResponse : HttpResponse
{
    private HttpContext? _httpContext;

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException("The HttpContext back-reference has not been attached.");

    internal void AttachContext(HttpContext context) => _httpContext ??= context;
}

internal sealed class TestHttpContext : HttpContext
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

/// <summary>
/// A recording handler used to assert dispatch. Returns a configurable authenticate result and
/// records every verb invocation with its arguments.
/// </summary>
internal sealed class RecordingAuthenticationHandler : IAuthenticationSignInHandler
{
    public RecordingAuthenticationHandler(AuthenticateResult? authenticateResult = null)
    {
        AuthenticateResult = authenticateResult ?? Authentication.AuthenticateResult.NoResult();
    }

    public AuthenticateResult AuthenticateResult { get; set; }
    public AuthenticationScheme? Scheme { get; private set; }
    public int InitializeCount { get; private set; }
    public int AuthenticateCount { get; private set; }
    public int ChallengeCount { get; private set; }
    public int ForbidCount { get; private set; }
    public ClaimsPrincipal? SignedInUser { get; private set; }
    public int SignOutCount { get; private set; }

    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        Scheme = scheme;
        InitializeCount++;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        AuthenticateCount++;
        return Task.FromResult(AuthenticateResult);
    }

    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        ChallengeCount++;
        return Task.CompletedTask;
    }

    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        ForbidCount++;
        return Task.CompletedTask;
    }

    public Task SignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        SignedInUser = user;
        return Task.CompletedTask;
    }

    public Task SignOutAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        SignOutCount++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// An authenticate-only handler (no sign-in support) used to prove sign-in dispatch rejects
/// schemes whose handler is not an <see cref="IAuthenticationSignInHandler"/>.
/// </summary>
internal sealed class AuthenticateOnlyHandler : IAuthenticationHandler
{
    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AuthenticateResult.NoResult());

    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
