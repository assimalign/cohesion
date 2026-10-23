using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Exe.Internal;

[global::System.Diagnostics.DebuggerDisplay("BoundedCapture")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BoundedCapture : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _limit;
    private int _count;

    internal BoundedCapture(int limit)
    {
        _limit = limit;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, limit));
    }

    internal bool Truncated { get; private set; }
    internal string Text => Encoding.UTF8.GetString(_buffer, 0, _count);

    internal async Task DrainAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(scratch.AsMemory(0, 8192), cancellationToken).ConfigureAwait(false)) != 0)
            {
                var retained = Math.Min(read, _limit - _count);
                scratch.AsSpan(0, retained).CopyTo(_buffer.AsSpan(_count));
                _count += retained;
                Truncated |= retained < read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(scratch, clearArray: true); }
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);

    private sealed class DebugView
    {
        public DebugView(BoundedCapture value)
        {
        }
    }
}
