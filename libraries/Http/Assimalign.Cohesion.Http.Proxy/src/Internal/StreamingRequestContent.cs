using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The forwarded request's body: copies the client's request body to the destination as it arrives,
/// flushing each read so a streaming upload (gRPC, a long poll) is not held back.
/// </summary>
/// <remarks>
/// The length is never computed: when the client declared one, the forwarder copies its
/// <c>Content-Length</c> onto <see cref="HttpContent.Headers"/>; otherwise the body is sent chunked,
/// or in HTTP/2 and HTTP/3 data frames. A failure to read the client's body is recorded in
/// <see cref="SourceFailed"/>, because the invoker reports it as its own failure.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("StreamingRequestContent {SourceFailed}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class StreamingRequestContent : HttpContent
{
    private const int bufferSize = 16 * 1024;

    private readonly Stream _source;
    private readonly ActivityTimeout _activity;
    private int _started;
    private volatile bool _sourceFailed;

    public StreamingRequestContent(Stream source, ActivityTimeout activity)
    {
        _source = source;
        _activity = activity;
    }

    /// <summary>
    /// Gets a value indicating whether reading the client's body failed or stalled past the activity
    /// timeout.
    /// </summary>
    public bool SourceFailed => _sourceFailed;

    /// <summary>
    /// Gets a value indicating whether the invoker started sending the body.
    /// </summary>
    public bool Started => Volatile.Read(ref _started) != 0;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        return SerializeToStreamAsync(stream, context, CancellationToken.None);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        // The client's body can be read once; a redirect or retry that asks again cannot be served.
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(ErrorMessages.RequestContentReused);
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _activity.Token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (true)
            {
                int read;

                try
                {
                    read = await _source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (RecordSourceFailure(exception, cancellationToken))
                {
                    // Never entered: the filter records the failure and lets the exception propagate.
                    throw;
                }

                if (read == 0)
                {
                    return;
                }

                _activity.Restart();
                await stream.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                await stream.FlushAsync(linked.Token).ConfigureAwait(false);
                _activity.Restart();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    // A read the invoker canceled is the invoker's decision, not the client's failure; a read the
    // activity timeout canceled means the client stopped sending.
    private bool RecordSourceFailure(Exception exception, CancellationToken invokerToken)
    {
        if (exception is not OperationCanceledException || (_activity.HasTimedOut && !invokerToken.IsCancellationRequested))
        {
            _sourceFailed = true;
        }

        return false;
    }

    private sealed class DebugView
    {
        private readonly StreamingRequestContent _target;

        public DebugView(StreamingRequestContent value)
        {
            _target = value;
        }

        public HttpContentHeaders Headers => _target.Headers;
        public bool SourceFailed => _target.SourceFailed;
        public bool Started => _target.Started;
    }
}
