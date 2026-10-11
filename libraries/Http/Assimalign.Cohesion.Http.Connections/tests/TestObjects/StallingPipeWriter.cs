using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="PipeWriter"/> over another whose flushes complete only within a credit the test grants:
/// the backpressure a transport applies when its peer stops reading (a full TCP send buffer) or grants no
/// flow-control credit (QUIC <c>MAX_STREAM_DATA</c>). Writes reach the inner writer at once, but a flush
/// waits until every octet written so far is within the credit, and only then flushes the inner writer,
/// so the peer sees nothing the credit does not cover.
/// </summary>
/// <remarks>
/// The credit starts unlimited, so a connection can open and exchange its preface before the test calls
/// <see cref="Stall"/>. A flush cancelled while it waits throws an <see cref="OperationCanceledException"/>,
/// as a pipe's flush does.
/// </remarks>
internal sealed class StallingPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;
    private readonly Lock _gate = new();
    private long _written;
    private long _credit = long.MaxValue;
    private bool _isFlushWaiting;
    private TaskCompletionSource _creditGranted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StallingPipeWriter(PipeWriter inner)
    {
        _inner = inner;
    }

    /// <summary>Gets the octets written so far.</summary>
    public long Written
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a flush is waiting for credit: the writer is stalled.
    /// </summary>
    public bool IsFlushWaiting
    {
        get => Volatile.Read(ref _isFlushWaiting);
        private set => Volatile.Write(ref _isFlushWaiting, value);
    }

    /// <summary>
    /// Stops accepting octets: everything written so far is covered, and nothing more until
    /// <see cref="Grant"/>.
    /// </summary>
    public void Stall()
    {
        lock (_gate)
        {
            _credit = _written;
        }
    }

    /// <summary>
    /// Accepts <paramref name="octets"/> more octets, releasing a flush they cover.
    /// </summary>
    /// <param name="octets">The octets to accept.</param>
    public void Grant(long octets)
    {
        TaskCompletionSource granted;

        lock (_gate)
        {
            _credit = _credit == long.MaxValue ? long.MaxValue : _credit + octets;
            granted = _creditGranted;
            _creditGranted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        granted.TrySetResult();
    }

    /// <inheritdoc />
    public override void Advance(int bytes)
    {
        lock (_gate)
        {
            _written += bytes;
        }

        _inner.Advance(bytes);
    }

    /// <inheritdoc />
    public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

    /// <inheritdoc />
    public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

    /// <inheritdoc />
    public override void CancelPendingFlush() => _inner.CancelPendingFlush();

    /// <inheritdoc />
    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    /// <inheritdoc />
    public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

    /// <inheritdoc />
    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task granted;

            lock (_gate)
            {
                if (_written <= _credit)
                {
                    IsFlushWaiting = false;
                    break;
                }

                IsFlushWaiting = true;
                granted = _creditGranted.Task;
            }

            await granted.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
