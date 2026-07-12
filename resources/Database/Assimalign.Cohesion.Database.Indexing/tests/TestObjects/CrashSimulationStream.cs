using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

/// <summary>
/// A stream modeling crash semantics for the index crash suites (mirrors the
/// storage test harness): writes survive a crash only when flushed, or immediately
/// in write-through mode (worst-case steal).
/// </summary>
public sealed class CrashSimulationStream : Stream
{
    private readonly MemoryStream _live = new();
    private readonly bool _writeThrough;
    private byte[] _durable = Array.Empty<byte>();

    public CrashSimulationStream(bool writeThrough = false)
    {
        _writeThrough = writeThrough;
    }

    public byte[] CaptureDurable() => (byte[])_durable.Clone();

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _live.Length;

    public override long Position
    {
        get => _live.Position;
        set => _live.Position = value;
    }

    public override void Flush() => _durable = _live.ToArray();

    public override int Read(byte[] buffer, int offset, int count) => _live.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => _live.Seek(offset, origin);

    public override void SetLength(long value)
    {
        _live.SetLength(value);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _live.Write(buffer, offset, count);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }
}
