using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

internal class GeneratedContentStream : Stream
{
    private readonly long _length;

    /// <summary>Initializes a new instance of the <see cref="GeneratedContentStream"/> class.</summary>
    /// <param name="length">The total number of generated content bytes the stream yields.</param>
    public GeneratedContentStream(long length)
    {
        _length = length;
    }

    internal long BytesRead { get; private set; }
    internal int LargestReadRequest { get; private set; }
    internal bool WasDisposed { get; private set; }
    public override bool CanRead => !WasDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
        int count = (int)Math.Min(buffer.Length, _length - BytesRead);
        for (int index = 0; index < count; index++)
        {
            buffer.Span[index] = ContentByte(BytesRead + index);
        }
        BytesRead += count;
        return ValueTask.FromResult(count);
    }

    internal static byte ContentByte(long position) => unchecked((byte)(position * 31 + position / 251 + 17));
    protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

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
