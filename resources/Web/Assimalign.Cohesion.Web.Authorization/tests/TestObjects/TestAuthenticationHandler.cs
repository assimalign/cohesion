using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;

namespace Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

/// <summary>
/// A header-driven authentication scheme for the end-to-end tests. A request authenticates under the
/// scheme when it carries <c>X-Test-User-{scheme}: name[;role=r][;claim=type:value]...</c>. A challenge
/// answers <c>401</c> and appends the scheme to <c>WWW-Authenticate</c> (RFC 9110 §11.6.1 lists several
/// challenges comma-separated); a forbid answers <c>403</c> and appends the scheme to
/// <see cref="ForbiddenHeader"/>, so a test can tell which schemes answered.
/// </summary>
internal sealed class TestAuthenticationHandler : IAuthenticationHandler
{
    /// <summary>The response header a forbid appends its scheme name to.</summary>
    public const string ForbiddenHeader = "X-Test-Forbidden";

    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;

    /// <summary>The request header that carries the user for <paramref name="scheme"/>.</summary>
    public static string UserHeader(string scheme) => "X-Test-User-" + scheme;

    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        _scheme = scheme;
        _context = context;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        string? value = _context.Request.Headers.GetValue(UserHeader(_scheme.Name));

        if (string.IsNullOrWhiteSpace(value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string[] segments = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The scheme name is the authentication type, so the identity reports IsAuthenticated.
        ClaimsIdentity identity = new(_scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.Name, segments[0]));

        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i];

            if (segment.StartsWith("role=", StringComparison.Ordinal))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, segment["role=".Length..]));
            }
            else if (segment.StartsWith("claim=", StringComparison.Ordinal))
            {
                string claim = segment["claim=".Length..];
                int separator = claim.IndexOf(':', StringComparison.Ordinal);
                identity.AddClaim(new Claim(claim[..separator], claim[(separator + 1)..]));
            }
        }

        AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), properties: null, _scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        _context.Response.StatusCode = HttpStatusCode.Unauthorized;
        Append(HttpHeaderKey.WWWAuthenticate, _scheme.Name);
        return Task.CompletedTask;
    }

    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        _context.Response.StatusCode = HttpStatusCode.Forbidden;
        Append(ForbiddenHeader, _scheme.Name);
        return Task.CompletedTask;
    }

    private void Append(HttpHeaderKey key, string value)
    {
        string? existing = _context.Response.Headers.GetValue(key);
        _context.Response.Headers[key] = string.IsNullOrEmpty(existing) ? value : existing + ", " + value;
    }
}
