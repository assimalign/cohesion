using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// Stands in for the transport's <see cref="IHttpResponseStreamingFeature"/> while the security-headers
/// middleware is on the stack, so a streamed response carries the headers. A streamed head is committed
/// by the first start, write, flush or complete, inside the handler and long before the middleware
/// regains control; this decorator stages the headers immediately before that first call and then
/// delegates. It is the pipeline-level equivalent of a response-starting callback, which the Web stack
/// does not offer.
/// </summary>
/// <remarks>
/// The decorator reports the inner feature's <see cref="IHttpFeature.Name"/>, so installing it replaces
/// the inner feature in its slot, and restoring the inner feature replaces the decorator. Like the
/// feature it wraps, it is not safe for concurrent use: one exchange's response has one writer.
/// </remarks>
internal sealed class SecurityHeadersStreamingFeature : IHttpResponseStreamingFeature
{
    private readonly IHttpResponseStreamingFeature _inner;
    private readonly SecurityHeadersMiddleware _middleware;
    private readonly IHttpContext _context;
    private readonly ISecurityHeadersFeature _feature;
    private bool _isStaged;

    public SecurityHeadersStreamingFeature(
        IHttpResponseStreamingFeature inner,
        SecurityHeadersMiddleware middleware,
        IHttpContext context,
        ISecurityHeadersFeature feature)
    {
        _inner = inner;
        _middleware = middleware;
        _context = context;
        _feature = feature;
    }

    /// <summary>Gets the transport's feature this decorator stands in for.</summary>
    public IHttpResponseStreamingFeature Inner => _inner;

    /// <summary>Gets whether the headers were staged on the streamed head.</summary>
    public bool IsStaged => _isStaged;

    public string Name => _inner.Name;

    public bool HasStarted => _inner.HasStarted;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Stage();
        return _inner.StartAsync(cancellationToken);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Stage();
        return _inner.WriteAsync(data, cancellationToken);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        Stage();
        return _inner.FlushAsync(cancellationToken);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Stage();
        return _inner.CompleteAsync(cancellationToken);
    }

    private void Stage()
    {
        if (_isStaged || _inner.HasStarted)
        {
            return;
        }

        _middleware.Stage(_context, _feature);
        _isStaged = true;
    }
}
