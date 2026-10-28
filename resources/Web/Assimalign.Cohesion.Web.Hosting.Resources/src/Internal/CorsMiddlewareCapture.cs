using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Cors;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

/// <summary>
/// Builds Web.Cors' middleware for a <see cref="WebCorsPolicy"/>. <c>UseCors</c> is the only way to
/// create it, so this pipeline builder runs <c>UseCors</c> and keeps the middleware it adds instead
/// of composing a pipeline.
/// </summary>
internal sealed class CorsMiddlewareCapture : IWebApplicationPipelineBuilder
{
    private IWebApplicationMiddleware? _middleware;

    private CorsMiddlewareCapture()
    {
    }

    /// <summary>
    /// Creates the CORS middleware that enforces a policy.
    /// </summary>
    /// <exception cref="ArgumentException">The policy names an invalid origin, method, or header.</exception>
    /// <exception cref="InvalidOperationException">The policy combines settings CORS forbids.</exception>
    public static IWebApplicationMiddleware Create(WebCorsPolicy policy)
    {
        var capture = new CorsMiddlewareCapture();
        capture.UseCors(options => Configure(options, policy));

        return capture._middleware!;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
    {
        _middleware = middleware;
        return this;
    }

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        throw new NotSupportedException();
    }

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        throw new NotSupportedException();
    }

    public IWebApplicationPipeline Build()
    {
        throw new NotSupportedException();
    }

    private static void Configure(CorsOptions options, WebCorsPolicy policy)
    {
        if (policy.Origins is not { Count: > 0 })
        {
            return;
        }

        options.AddDefaultPolicy(builder =>
        {
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
        });
    }

    private static bool IsAny(IReadOnlyList<string>? values)
    {
        return values is { Count: 1 } && values[0] == "*";
    }
}
