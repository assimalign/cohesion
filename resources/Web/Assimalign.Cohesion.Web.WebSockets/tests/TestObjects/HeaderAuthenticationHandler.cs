using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// A header-driven authentication scheme: a request authenticates when it carries
/// <c>X-Test-User: name[;role=r]...</c>. A challenge answers <c>401</c>, a forbid <c>403</c>.
/// </summary>
internal sealed class HeaderAuthenticationHandler : IAuthenticationHandler
{
    /// <summary>The scheme name.</summary>
    public const string Scheme = "Test";

    /// <summary>The request header that carries the user.</summary>
    public const string UserHeader = "X-Test-User";

    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;

    /// <summary>Creates the scheme registration.</summary>
    public static AuthenticationScheme CreateScheme() => new(Scheme, displayName: null, () => new HeaderAuthenticationHandler());

    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        _scheme = scheme;
        _context = context;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        string? value = _context.Request.Headers.GetValue(UserHeader);

        if (string.IsNullOrWhiteSpace(value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string[] segments = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        ClaimsIdentity identity = new(_scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.Name, segments[0]));

        for (int i = 1; i < segments.Length; i++)
        {
            if (segments[i].StartsWith("role=", StringComparison.Ordinal))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, segments[i]["role=".Length..]));
            }
        }

        AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), properties: null, _scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
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
