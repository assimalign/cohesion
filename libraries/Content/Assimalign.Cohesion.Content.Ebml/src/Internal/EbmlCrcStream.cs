using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Ebml.Internal;

[global::System.Diagnostics.DebuggerDisplay("EbmlCrcStream")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class EbmlCrcStream : Stream
{
    private uint _state = uint.MaxValue;
    internal uint Value => ~_state;
    public override bool CanRead => false;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(ReadOnlySpan<byte> buffer) => _state = EbmlCrc32.Update(_state, buffer);
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private sealed class DebugView
    {
        private readonly EbmlCrcStream _target;

        public DebugView(EbmlCrcStream value)
        {
            _target = value;
        }

        public bool CanRead => _target.CanRead;
        public bool CanWrite => _target.CanWrite;
        public bool CanSeek => _target.CanSeek;
        public bool CanTimeout => _target.CanTimeout;
        public int ReadTimeout => _target.ReadTimeout;
        public int WriteTimeout => _target.WriteTimeout;
    }
}
