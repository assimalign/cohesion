using System.Diagnostics;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The current request's id: the W3C trace id of the trace the exchange belongs to.
/// </summary>
/// <remarks>
/// <para>
/// One value names the request in the server's span, in the application's logs and in anything it
/// returns to the caller, so a request found in one can be found in the others. The default server
/// installs this feature on every exchange before the pipeline runs:
/// </para>
/// <list type="bullet">
/// <item>When the server traces the request (a listener subscribes to its <see cref="ActivitySource"/>,
/// <c>Assimalign.Cohesion.Web.Hosting</c>), the id is the trace id of the request's server span. That is
/// the caller's trace id when the request carried a valid W3C <c>traceparent</c> header, and a new trace
/// id otherwise.</item>
/// <item>Without a span the same rule applies: the trace id of a valid <c>traceparent</c>, or else a
/// random trace id, generated the first time the id is read.</item>
/// </list>
/// <para>
/// The id is stable for the exchange. <see cref="ActivityTraceId.ToHexString"/> returns the
/// 32-character lower-case form that <c>traceparent</c>, OTLP and log correlation use. A custom
/// <see cref="IWebApplicationServer"/> may omit the feature, so middleware must handle an absent
/// feature.
/// </para>
/// </remarks>
public interface IWebRequestIdFeature : IHttpFeature
{
    /// <summary>
    /// Gets the request's id: the trace id of the trace the exchange belongs to.
    /// </summary>
    ActivityTraceId RequestId { get; }
}
