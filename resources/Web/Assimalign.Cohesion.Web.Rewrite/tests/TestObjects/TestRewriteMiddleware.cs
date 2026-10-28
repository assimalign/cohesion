using System;

namespace Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

/// <summary>
/// Builds the rewrite middleware through <see cref="RewriteBuilder"/>, as
/// <c>builder.Services.AddRewrite(rewrite =&gt; ...)</c> does, so a test can place it directly with
/// <see cref="IWebApplicationPipelineBuilder.Use(IWebApplicationMiddleware)"/>. That also composes two
/// differently configured rewrites in one pipeline, which <c>UseRewrite()</c> cannot: it places the one
/// registration the application made.
/// </summary>
internal static class TestRewriteMiddleware
{
    public static IWebApplicationMiddleware Build(Action<RewriteOptions> configure)
    {
        RewriteBuilder rewrite = new();
        configure(rewrite.Options);

        return rewrite.Build();
    }
}
