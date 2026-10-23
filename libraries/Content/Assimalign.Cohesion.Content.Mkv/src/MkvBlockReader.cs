using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Mkv.Internal;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>Reads one SimpleBlock or Block's frames forwards without buffering encoded frame data.</summary>
/// <remarks>Compose with EbmlReader and MkvSchema.Registry. Consume frames sequentially before advancing
/// that reader. Disposing this reader closes its borrowed payload view; the EBML reader drains unread
/// bytes when it advances. Track metadata, cluster timestamps and BlockGroup references remain the
/// enclosing reader's responsibility.</remarks>
[global::System.Diagnostics.DebuggerDisplay("MkvBlockReader")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class MkvBlockReader : IDisposable
{
    private readonly ContentSliceStream _payload;
    private readonly MkvBlockHeader _header;
    private readonly long _origin;
    private ContentSliceStream? _frame;
    private int _nextFrame;
    private bool _disposed;

    private MkvBlockReader(ContentSliceStream payload, MkvBlockHeader header, long origin)
    {
        _payload = payload; _header = header; _origin = origin;
        FrameOffsets = Array.AsReadOnly(header.Offsets);
    }

    /// <summary>Opens and parses the current EBML block's bounded payload stream.</summary>
    /// <param name="reader">The EBML reader positioned on SimpleBlock or Block.</param>
    /// <param name="maxFrames">The permitted frame count, from one through 256.</param>
    /// <returns>A caller-disposable frame reader borrowing the EBML reader's lifetime.</returns>
    /// <exception cref="ArgumentNullException">The EBML reader is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame limit is invalid.</exception>
    /// <exception cref="InvalidOperationException">The current element is not a block or its payload is already open.</exception>
    /// <exception cref="ObjectDisposedException">The EBML reader is disposed.</exception>
    /// <exception cref="ContentFormatException">The block or lacing header is truncated, malformed, or exceeds the cap.</exception>
    public static MkvBlockReader Create(EbmlReader reader, int maxFrames = 256)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFrames, 256);
        var element = reader.Current;
        if (element.Identifier.EncodedValue is not (0xA3 or 0xA1))
        {
            throw new InvalidOperationException(ErrorMessages.BlockRequired);
        }
        var payload = reader.OpenPayloadStream();
        try
        {
            var header = MkvBlockHeader.Read(payload, payload.Length, element.DataOffset,
                element.Identifier.EncodedValue == 0xA3, maxFrames);
            return new MkvBlockReader(payload, header, element.DataOffset);
        }
        catch
        {
            // Cleanup only: preserve every original parse/I/O exception.
            payload.Dispose();
            throw;
        }
    }

    /// <summary>Gets the nonzero TrackNumber.</summary>
    public ulong TrackNumber => _header.TrackNumber;
    /// <summary>Gets the signed block timestamp relative to the cluster, in segment ticks.</summary>
    public short RelativeTimestamp => _header.RelativeTimestamp;
    /// <summary>Gets the SimpleBlock keyframe flag; BlockGroup sync status depends on ReferenceBlock.</summary>
    public bool IsKeyframe => _header.IsSimple && (_header.Flags & 0x80) != 0;
    /// <summary>Gets the invisible flag.</summary>
    public bool IsInvisible => (_header.Flags & 8) != 0;
    /// <summary>Gets the SimpleBlock discardable flag.</summary>
    public bool IsDiscardable => _header.IsSimple && (_header.Flags & 1) != 0;
    /// <summary>Gets the lacing mode.</summary>
    public MkvLacing Lacing => (MkvLacing)((_header.Flags >> 1) & 3);
    /// <summary>Gets the validated number of frames.</summary>
    public int FrameCount => _header.Sizes.Length;
    /// <summary>Gets absolute frame offsets, including on a non-seekable source.</summary>
    public IReadOnlyList<long> FrameOffsets { get; }

    /// <summary>Drains the previous frame's remainder and opens the next bounded frame stream.</summary>
    /// <returns>A caller-disposable forward-only frame stream, or null after all frames.</returns>
    /// <remarks>Streams support asynchronous reads and cancellation. Advancing invalidates the previous
    /// stream, including one disposed early. Stream cursors are relative; FrameOffsets supplies absolute positions.</remarks>
    /// <exception cref="ObjectDisposedException">This reader or the enclosing EBML payload is disposed or advanced.</exception>
    /// <exception cref="ContentFormatException">Draining encounters truncated encoded frame bytes.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public ContentSliceStream? ReadFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = _nextFrame < FrameCount ? _header.Offsets[_nextFrame] - _origin : _payload.Length;
        _payload.SkipExactly(target - _payload.Position);
        _frame?.Dispose();
        _frame = null;
        if (_nextFrame == FrameCount) { return null; }
        _frame = _payload.CreateSlice(target, _header.Sizes[_nextFrame]);
        _nextFrame++;
        return _frame;
    }

    /// <summary>Invalidates frame streams without draining or closing the borrowed EBML reader or source.</summary>
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _frame?.Dispose();
        _payload.Dispose();
    }

    private sealed class DebugView
    {
        private readonly MkvBlockReader _target;

        public DebugView(MkvBlockReader value)
        {
            _target = value;
        }

        public ulong TrackNumber => _target.TrackNumber;
        public short RelativeTimestamp => _target.RelativeTimestamp;
        public bool IsKeyframe => _target.IsKeyframe;
        public bool IsInvisible => _target.IsInvisible;
        public bool IsDiscardable => _target.IsDiscardable;
        public MkvLacing Lacing => _target.Lacing;
        public int FrameCount => _target.FrameCount;
        public IReadOnlyList<long> FrameOffsets => _target.FrameOffsets;
    }
}
