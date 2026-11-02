using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ErrorHandling.Internal;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// Configures the status-code-pages middleware that
/// <c>builder.Services.AddStatusCodePages(pages =&gt; ...)</c> registers and
/// <see cref="ErrorHandlingPipelineExtensions.UseStatusCodePages(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <see cref="Build"/> copies the options into the middleware, so changing them afterwards has no effect.
/// </remarks>
public sealed class StatusCodePagesBuilder
{
    /// <summary>
    /// Initializes a builder with the default: a bodyless <c>4xx</c>/<c>5xx</c> response is rendered as
    /// RFC 9457 problem+json for its status.
    /// </summary>
    public StatusCodePagesBuilder()
    {
    }

    /// <summary>
    /// Gets the options: chiefly a custom responder in place of the problem+json default.
    /// </summary>
    public StatusCodePagesOptions Options { get; } = new();

    /// <summary>
    /// Builds the status-code-pages middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which renders a bodyless, unstarted <c>4xx</c>/<c>5xx</c> response through the
    /// configured responder, or as problem+json without one, and leaves every other response untouched.
    /// </returns>
    public IWebApplicationMiddleware Build()
    {
        return new StatusCodePagesMiddleware(new StatusCodePagesOptions
        {
            Responder = Options.Responder,
        });
    }
}
