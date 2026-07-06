using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// Builder-time configuration for the status-code-pages middleware.
/// </summary>
public sealed class StatusCodePagesOptions
{
    /// <summary>
    /// Gets or sets the responder invoked for a bodyless 4xx/5xx terminal response. When
    /// <see langword="null"/> (the default) the middleware writes RFC 9457 problem+json for the
    /// current status code. Set a custom responder to render a different body or to re-execute the
    /// pipeline against an error path.
    /// </summary>
    public Func<IHttpContext, Task>? Responder { get; set; }
}
