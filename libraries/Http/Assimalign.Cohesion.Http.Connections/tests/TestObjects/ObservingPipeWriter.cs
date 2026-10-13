using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>Observes output writes and asynchronous completion before forwarding them to the transport.</summary>
internal sealed class ObservingPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;

    public ObservingPipeWriter(PipeWriter inner)
    {
        _inner = inner;
    }

    public Func<ValueTask>? CompletingAsync { get; set; }
    public Func<ReadOnlyMemory<byte>, ValueTask>? WritingAsync { get; set; }

    public override void Advance(int bytes) => _inner.Advance(bytes);
    public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
    public override void CancelPendingFlush() => _inner.CancelPendingFlush();
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        => _inner.FlushAsync(cancellationToken);
    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (CompletingAsync is { } completing)
        {
            await completing().ConfigureAwait(false);
        }
        await _inner.CompleteAsync(exception).ConfigureAwait(false);
    }

    public override async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        if (WritingAsync is { } writing)
        {
            await writing(source).ConfigureAwait(false);
        }
        return await _inner.WriteAsync(source, cancellationToken).ConfigureAwait(false);
    }
}
