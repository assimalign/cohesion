using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>Registers work to run immediately before the final response head is serialized.</summary>
/// <remarks>
/// Callbacks run once per exchange in reverse registration order, for buffered and streamed
/// responses. Protocol takeovers and transport-generated rejection heads are excluded.
/// They may stage status and headers, but must not start, write, complete or take over
/// the response themselves. A callback failure aborts the exchange before the transport writes
/// its head; remaining callbacks do not run. Registration and response writes are single-consumer.
/// If the transport refuses the head, callback header changes and explicitly preserved headers
/// are carried onto a buffered replacement, including a server-generated error, without invoking
/// any callback again. Framing and representation metadata belong to the replacement and are
/// never replayed, including explicitly preserved fields. Other application headers belong to
/// the refused response.
/// The default Web server publishes this feature; a custom server must implement it to support
/// middleware that requires response-starting callbacks.
/// </remarks>
public interface IWebResponseStartingFeature : IHttpFeature
{
    /// <summary>Registers an asynchronous callback and its state before response starting begins.</summary>
    /// <param name="callback">The callback that stages the final response head.</param>
    /// <param name="state">The state passed to the callback.</param>
    /// <param name="preservedHeaders">
    /// Optional header keys whose final values, or absence, must survive a refused head even when
    /// unchanged by the callbacks. Keys are copied at registration; values are captured after all
    /// callbacks succeed. Use this for policy headers that accept an existing application value.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Starting callbacks have begun or the response has started.</exception>
    void OnStarting(Func<object?, Task> callback, object? state,
        IReadOnlyCollection<HttpHeaderKey>? preservedHeaders = null);
}
