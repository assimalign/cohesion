using System;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

/// <summary>
/// A typed <see cref="IHttpFeature"/> that surfaces the exception a pipeline exception boundary
/// caught, plus the request path at the point of the fault. The exception-boundary middleware sets
/// this on <see cref="IHttpContext.Features"/> so downstream error handlers, diagnostics, and
/// custom error pages can inspect the fault without it being re-thrown or passed out of band.
/// </summary>
/// <remarks>
/// <para>
/// This is the Web-layer analogue of the wire-level failure classification that lives in
/// <c>Assimalign.Cohesion.Http.Connections</c>: the protocol core deliberately carries no
/// application-exception concept, so the caught <em>application</em> exception is exposed here in the
/// Web composition layer rather than in the HTTP protocol core. It is a neutral capability slot &#8212;
/// it carries the exception, not a rendering policy; translating it into a response (for example
/// RFC 9457 problem+json) is the boundary middleware's job.
/// </para>
/// </remarks>
public interface IHttpExceptionFeature : IHttpFeature
{
    /// <summary>
    /// Gets the exception caught by the boundary.
    /// </summary>
    Exception Error { get; }

    /// <summary>
    /// Gets the request path at the point the exception was caught.
    /// </summary>
    HttpPath Path { get; }
}
