using System;

using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// The <c>nonfile</c> policy: the value's last path segment must not name a file, meaning it has no
/// file extension (a <c>.</c> followed by at least one character). A fallback route uses it on its
/// catch-all (<c>{**path:nonfile}</c>) so a request for a missing asset such as <c>/app.js</c> is not
/// answered by the fallback and still reaches the 404.
/// </summary>
internal sealed class NonFileRouteParameterPolicy : RouteParameterPolicy
{
    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.TryGetParameterValue(out object? value) || value is not string text || text.Length == 0)
        {
            // An absent or empty catch-all names the directory itself, which is not a file.
            return true;
        }

        // The matcher drops a trailing '/', which is what marks a directory: '/v1.2/' names no file.
        if (context.HttpContext?.Request.Path.Value is { Length: > 1 } requestPath && requestPath[^1] == '/')
        {
            return true;
        }

        ReadOnlySpan<char> lastSegment = text.AsSpan(text.LastIndexOf('/') + 1);
        int dot = lastSegment.LastIndexOf('.');

        return dot < 0 || dot == lastSegment.Length - 1;
    }
}
