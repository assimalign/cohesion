using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Default <see cref="IHttpResponseStreamingFeature"/> implementation. A thin typed API over the
/// transport's raw response body sink (<see cref="HttpExchangeInterceptorResponseContext.ResponseBody"/>):
/// the sink owns the wire framing and header-commit timing, while this feature owns the ergonomic
/// start/write/flush/complete surface and the write-after-complete guard.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HttpResponseStreamingFeature {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class HttpResponseStreamingFeature : IHttpResponseStreamingFeature
{
    /// <summary>The name under which the response-streaming feature is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.ResponseStreaming";

    private readonly Stream _sink;
    private readonly IHttpExchangeControl? _control;
    private bool _started;
    private bool _completed;

    public HttpResponseStreamingFeature(Stream sink, IHttpExchangeControl? control)
    {
        _sink = sink;
        _control = control;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    // The transport knows whether a failed write refused the head or may have put bytes on the wire.
    // Only contexts without that generic control need a local success latch.
    public bool HasStarted => _control?.HasResponseStarted ?? _started;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (HasStarted)
        {
            return;
        }

        // Flushing the sink with no body written commits the head (the sink commits headers on the
        // first write or flush).
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        _started = true;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await _sink.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        _started = true;
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        _started = true;
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        // Flush the buffered bytes; the transport emits the wire terminator when it finalizes the
        // exchange (the sink's own completion).
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        _started = true;
        _completed = true;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                ErrorMessages.StreamingResponseAlreadyCompleted);
        }
    }

    private sealed class DebugView
    {
        private readonly HttpResponseStreamingFeature _target;

        public DebugView(HttpResponseStreamingFeature value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public bool HasStarted => _target.HasStarted;
    }
}
