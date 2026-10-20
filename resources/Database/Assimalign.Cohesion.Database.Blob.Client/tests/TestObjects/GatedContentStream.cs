using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

// The sender requests the second source chunk only after the receiver accepted
// the first, so this gate deterministically interrupts an actual partial write.
internal sealed class GatedContentStream : GeneratedContentStream
{
    internal GatedContentStream() : base(3L * BlobProtocol.MaxChunkLength) { }
    internal TaskCompletionSource FirstChunkAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (BytesRead != 0)
        {
            FirstChunkAccepted.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
        }
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
