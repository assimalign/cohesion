using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Quic.Internal;

/// <summary>
/// A <see cref="PipeWriter"/> for the unusable output half of a read-only stream: every write
/// operation throws, while completion members no-op so connection teardown stays uniform.
/// </summary>
internal sealed class UnwritablePipeWriter : PipeWriter
{
    /// <summary>
    /// The shared instance; the writer is stateless.
    /// </summary>
    public static readonly UnwritablePipeWriter Instance = new();

    private UnwritablePipeWriter()
    {
    }

    /// <inheritdoc />
    public override void Advance(int bytes) => throw NewWriteException();

    /// <inheritdoc />
    public override Memory<byte> GetMemory(int sizeHint = 0) => throw NewWriteException();

    /// <inheritdoc />
    public override Span<byte> GetSpan(int sizeHint = 0) => throw NewWriteException();

    /// <inheritdoc />
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => throw NewWriteException();

    /// <inheritdoc />
    public override void Complete(Exception? exception = null)
    {
        // No-op: completing the unusable half of a read-only stream is part of normal teardown.
    }

    /// <inheritdoc />
    public override void CancelPendingFlush()
    {
        // No-op: there is never a pending flush to cancel.
    }

    private static InvalidOperationException NewWriteException()
    {
        return new InvalidOperationException("The stream is read-only.");
    }
}
