using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Cors;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

/// <summary>
/// Converts between the <c>cors</c> action's state, a <see cref="WebCorsPolicy"/>, and the
/// <see cref="CorsPolicy"/> Web.Cors enforces.
/// </summary>
/// <remarks>
/// A policy with no origins is no default policy at all, in both directions, so CORS leaves the
/// application's requests alone. <c>*</c> alone in a list means any. A policy that admits origins
/// through a predicate has no data form; it reads as a policy with no origins.
/// </remarks>
internal static class WebCorsPolicyMapping
{
    private const string any = "*";

    /// <summary>
    /// Builds the policy Web.Cors enforces, or <see langword="null"/> for no default policy.
    /// </summary>
    /// <exception cref="ArgumentException">The policy names an invalid origin, method, or header.</exception>
    /// <exception cref="InvalidOperationException">The policy combines settings CORS forbids, such as any origin with credentials.</exception>
    public static CorsPolicy? ToPolicy(WebCorsPolicy policy)
    {
        if (policy.Origins is not { Count: > 0 })
        {
            return null;
        }

        CorsPolicyBuilder builder = new();

        if (IsAny(policy.Origins))
        {
            builder.AllowAnyOrigin();
        }
        else
        {
            builder.WithOrigins([.. policy.Origins]);
        }

        if (IsAny(policy.Methods))
        {
            builder.AllowAnyMethod();
        }
        else if (policy.Methods is { Count: > 0 })
        {
            builder.WithMethods([.. policy.Methods]);
        }

        if (IsAny(policy.Headers))
        {
            builder.AllowAnyHeader();
        }
        else if (policy.Headers is { Count: > 0 })
        {
            builder.WithHeaders(policy.Headers.Select(static header => (HttpFieldName)header).ToArray());
        }

        if (policy.ExposedHeaders is { Count: > 0 })
        {
            builder.WithExposedHeaders(policy.ExposedHeaders.Select(static header => (HttpFieldName)header).ToArray());
        }

        if (policy.AllowCredentials)
        {
            builder.AllowCredentials();
        }

        if (policy.PreflightMaxAgeSeconds is int seconds)
        {
            builder.SetPreflightMaxAge(TimeSpan.FromSeconds(seconds));
        }

        return builder.Build();
    }

    /// <summary>
    /// Describes the policy Web.Cors enforces; no default policy reads as a policy with no origins.
    /// </summary>
    public static WebCorsPolicy FromPolicy(CorsPolicy? policy)
    {
        if (policy is null)
        {
            return new WebCorsPolicy();
        }

        return new WebCorsPolicy
        {
            Origins = policy.AllowsAnyOrigin ? [any] : [.. policy.Origins],
            Methods = policy.AllowsAnyMethod ? [any] : [.. policy.Methods],
            Headers = policy.AllowsAnyHeader ? [any] : [.. policy.Headers.Select(static header => header.Value)],
            ExposedHeaders = [.. policy.ExposedHeaders.Select(static header => header.Value)],
            AllowCredentials = policy.SupportsCredentials,
            PreflightMaxAgeSeconds = policy.PreflightMaxAge is { } maxAge ? (int)maxAge.TotalSeconds : null,
        };
    }

    private static bool IsAny(IReadOnlyList<string>? values)
    {
        return values is { Count: 1 } && values[0] == any;
    }
}
