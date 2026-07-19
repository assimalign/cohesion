using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// Builder-time configuration for the status-code-pages middleware (<c>UseStatusCodePages</c>).
/// </summary>
public sealed class StatusCodePagesOptions
{
    /// <summary>
    /// Gets or sets the responder invoked for a bodyless <c>4xx</c>/<c>5xx</c> terminal response.
    /// When <see langword="null"/> (the default) the middleware writes RFC 9457 problem+json for the
    /// current status code. Set a custom responder to render a different body or to re-execute the
    /// pipeline against an error path. The responder runs only when the response is genuinely
    /// bodyless, so it never clobbers a body a handler already wrote.
    /// </summary>
    public Func<IHttpContext, Task>? Responder { get; set; }
}
