using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A stream that models crash semantics: bytes written are "in the OS page cache"
/// and survive a crash only if they were flushed. <see cref="CaptureDurable"/>
/// returns what would remain on disk after a power loss at that instant.
/// </summary>
/// <remarks>
/// In write-through mode every write is immediately durable — the worst case for a
/// steal-capable buffer pool, where the OS persists stolen page writes before the
/// transaction resolves. In the default mode only <see cref="Flush"/> makes prior
/// writes durable, which models losing journal appends that were never fsynced.
/// </remarks>
public sealed class CrashSimulationStream : Stream
{
    private readonly MemoryStream _live = new();
    private readonly bool _writeThrough;
    private byte[] _durable = Array.Empty<byte>();

    /// <summary>
    /// Initializes a crash-simulation stream.
    /// </summary>
    /// <param name="writeThrough">
    /// When true, every write is immediately durable (worst-case steal); when false,
    /// writes become durable only on flush.
    /// </param>
    public CrashSimulationStream(bool writeThrough = false)
    {
        _writeThrough = writeThrough;
    }

    /// <summary>
    /// Initializes a crash-simulation stream over existing durable content.
    /// </summary>
    /// <param name="content">The initial durable bytes.</param>
    /// <param name="writeThrough">Write-through durability mode.</param>
    public CrashSimulationStream(byte[] content, bool writeThrough = false)
        : this(writeThrough)
    {
        _live.Write(content);
        _live.Position = 0;
        _durable = (byte[])content.Clone();
    }

    /// <summary>
    /// Gets the number of flush calls observed (durability points).
    /// </summary>
    public int FlushCount { get; private set; }

    /// <summary>
    /// Returns the bytes that would survive a crash (power loss) right now.
    /// </summary>
    public byte[] CaptureDurable() => (byte[])_durable.Clone();

    /// <summary>
    /// Returns the live (post-crash-lost) content, for assertions on what was pending.
    /// </summary>
    public byte[] CaptureLive() => _live.ToArray();

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => _live.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _live.Position;
        set => _live.Position = value;
    }

    /// <inheritdoc />
    public override void Flush()
    {
        FlushCount++;
        _durable = _live.ToArray();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _live.Read(buffer, offset, count);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _live.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        _live.SetLength(value);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        _live.Write(buffer, offset, count);

        if (_writeThrough)
        {
            _durable = _live.ToArray();
        }
    }
}
