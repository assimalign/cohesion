using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Routing;

using NetHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

/// <summary>
/// Shared pieces of the end-to-end tests: the header-driven test schemes, an endpoint that reports the
/// principal it ran as, and request builders.
/// </summary>
internal static class TestEndpoints
{
    /// <summary>The default test scheme.</summary>
    public const string Primary = "Primary";

    /// <summary>A second test scheme, selected per endpoint.</summary>
    public const string Secondary = "Secondary";

    /// <summary>Creates a header-driven test scheme registration.</summary>
    public static AuthenticationScheme Scheme(string name) => new(name, displayName: null, () => new TestAuthenticationHandler());

    /// <summary>
    /// An endpoint that answers <c>200</c> with <c>ok:{name}</c> (the name of <c>context.User</c>, empty
    /// for an anonymous request) and, when given a box, counts its invocations.
    /// </summary>
    public static RouterRouteHandler Ok(StrongBox<int>? invocations = null) => new(async context =>
    {
        if (invocations is not null)
        {
            Interlocked.Increment(ref invocations.Value);
        }

        context.Response.StatusCode = HttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes("ok:" + (context.User.Identity?.Name ?? string.Empty)),
            context.RequestCancelled);
    });

    /// <summary>A request authenticated under the header-driven <paramref name="scheme"/>.</summary>
    /// <param name="method">The request method.</param>
    /// <param name="path">The request path.</param>
    /// <param name="scheme">The test scheme.</param>
    /// <param name="user">The user header value: <c>name[;role=r][;claim=type:value]</c>.</param>
    public static HttpRequestMessage Request(NetHttpMethod method, string path, string scheme, string user)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.TryAddWithoutValidation(TestAuthenticationHandler.UserHeader(scheme), user);
        return request;
    }

    /// <summary>A <c>GET</c> authenticated under the header-driven <paramref name="scheme"/>.</summary>
    public static HttpRequestMessage Get(string path, string scheme, string user)
        => Request(NetHttpMethod.Get, path, scheme, user);
}
