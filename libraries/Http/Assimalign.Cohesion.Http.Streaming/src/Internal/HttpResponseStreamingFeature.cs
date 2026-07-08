using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Default <see cref="IHttpResponseStreamingFeature"/> implementation. A thin typed API over the
/// transport's raw response body sink (<see cref="HttpResponseInterceptorContext.ResponseBody"/>):
/// the sink owns the wire framing and header-commit timing, while this feature owns the ergonomic
/// start/write/flush/complete surface and the write-after-complete guard.
/// </summary>
internal sealed class HttpResponseStreamingFeature : IHttpResponseStreamingFeature
{
    /// <summary>The name under which the response-streaming feature is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.ResponseStreaming";

    private readonly Stream _sink;
    private bool _started;
    private bool _completed;

    public HttpResponseStreamingFeature(Stream sink)
    {
        _sink = sink;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public bool HasStarted => _started;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        // Flushing the sink with no body written commits the head (the sink commits headers on the
        // first write or flush).
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        _started = true;
        await _sink.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        _started = true;
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        _started = true;
        _completed = true;
        // Flush the buffered bytes; the transport emits the wire terminator when it finalizes the
        // exchange (the sink's own completion).
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The streaming response has already been completed; no further body bytes can be written.");
        }
    }
}
