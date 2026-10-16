using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal sealed class Mp4SampleEntryFields
{
    private byte[] _prefix;
    private readonly int _length;
    private readonly int _limit;
    private readonly string _kind;

    internal Mp4SampleEntryFields(string kind, int length, int limit)
    {
        _kind = kind;
        _length = length;
        _limit = limit;
        _prefix = new byte[length];
        DataReferenceIndex = 1;
        if (kind == "Visual")
        {
            BinaryPrimitives.WriteUInt32BigEndian(_prefix.AsSpan(28), 72u << 16);
            BinaryPrimitives.WriteUInt32BigEndian(_prefix.AsSpan(32), 72u << 16);
            _prefix[41] = 1;
            _prefix[75] = 24;
            _prefix[76] = _prefix[77] = 255;
        }
        if (kind == "Audio") { ChannelCount = 2; _prefix[19] = 16; SampleRate = 48000; }
    }
    internal int Length => _prefix.Length;
    internal ushort DataReferenceIndex { get => Get16(6); set => Set16(6, value); }
    internal ushort Width { get => Get16(24); set => Set16(24, value); }
    internal ushort Height { get => Get16(26); set => Set16(26, value); }
    internal ushort ChannelCount { get => Get16(16); set => Set16(16, value); }
    internal double SampleRate
    {
        get => BinaryPrimitives.ReadUInt32BigEndian(_prefix.AsSpan(24)) / 65536d;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > uint.MaxValue / 65536d)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            BinaryPrimitives.WriteUInt32BigEndian(_prefix.AsSpan(24), (uint)(value * 65536d));
        }
    }
    internal string CompressorName
    {
        get => Encoding.Latin1.GetString(_prefix.AsSpan(43, _prefix[42]));
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > 31) { throw new ArgumentOutOfRangeException(nameof(value)); }
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] > 255) { throw new ArgumentException(ErrorMessages.CompressorNameInvalid, nameof(value)); }
            }
            _prefix.AsSpan(42, 32).Clear();
            _prefix[42] = (byte)value.Length;
            Encoding.Latin1.GetBytes(value, _prefix.AsSpan(43));
        }
    }
    internal void Read(BmffStream stream)
    {
        try { ReadCore(stream); }
        catch (ContentFormatException error) when (error.ErrorCode == ContentFormatErrorCode.Truncated)
        {
            throw new ContentFormatException(ErrorMessages.EntryInvalid, ContentFormatErrorCode.Truncated, stream.Offset + stream.Position, error);
        }
    }
    private void ReadCore(BmffStream stream)
    {
        if (_length > _limit)
        {
            throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, stream.Offset);
        }
        var fixedLength = _kind == "Xml" ? 8 : _length;
        _prefix = new byte[fixedLength];
        stream.ReadContentExactly(_prefix);
        if (_kind == "Visual" && _prefix[42] > 31)
        {
            throw new ContentFormatException(ErrorMessages.EntryInvalid, ContentFormatErrorCode.InvalidValue, stream.Offset + 42);
        }
        if (_kind == "Audio" && Get16(8) != 0)
        {
            throw new ContentFormatException(ErrorMessages.AudioVersionUnsupported, ContentFormatErrorCode.Unsupported, stream.Offset + 8);
        }
        if (_kind == "Xml")
        {
            var bytes = new List<byte>(_prefix);
            Span<byte> next = stackalloc byte[1];
            for (var field = 0; field < 3; field++)
            {
                while (true)
                {
                    if (bytes.Count >= _limit)
                    {
                        throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, stream.Offset + stream.Position);
                    }
                    stream.ReadContentExactly(next);
                    bytes.Add(next[0]);
                    if (next[0] == 0) { break; }
                }
            }
            _prefix = bytes.ToArray();
        }
    }
    internal void Write(BmffStream stream) => stream.Write(_prefix);
    private ushort Get16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_prefix.AsSpan(offset));
    private void Set16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(_prefix.AsSpan(offset), value);
}
