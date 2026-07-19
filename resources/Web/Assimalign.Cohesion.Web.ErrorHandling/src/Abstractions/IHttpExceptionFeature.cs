using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// The typed feature through which the exception boundary surfaces the fault it caught: the
/// exception that escaped the downstream pipeline, plus the request path at the point of the fault.
/// </summary>
/// <remarks>
/// <para>
/// The exception-boundary middleware (<c>UseErrorHandling</c>) sets this on
/// <see cref="IHttpContext.Features"/> before it consults the <c>OnError</c> chain, so registered
/// <see cref="IErrorHandler"/> handlers, a diagnostics observer, or a custom error page can inspect
/// the fault without it being re-thrown or passed out of band. It is a neutral capability slot — it
/// carries the exception, not a rendering policy; translating it into a response (RFC 9457
/// problem+json, say) is the boundary and the <c>OnError</c> chain's job.
/// </para>
/// <para>
/// This is the Web-layer analogue of the wire-level failure classification that lives in
/// <c>Assimalign.Cohesion.Http.Connections</c>: the protocol core deliberately carries no
/// application-exception concept, so the caught <em>application</em> exception is exposed here in the
/// Web feature layer rather than in the HTTP protocol core. It is homed in this feature package (not
/// the Web area root) per the area's rule that feature contracts live with their feature; any other
/// feature that wants the fault takes an ordinary cross-feature reference to this package.
/// </para>
/// </remarks>
public interface IHttpExceptionFeature : IHttpFeature
{
    /// <summary>
    /// Gets the exception the boundary caught.
    /// </summary>
    Exception Error { get; }

    /// <summary>
    /// Gets the request path at the point the exception was caught.
    /// </summary>
    HttpPath Path { get; }
}
