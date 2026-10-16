using System;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Internal;

// iref's version supplies the ID width. Decode after the parent assigns that context.
internal sealed class HeifReferenceFields
{
    private byte[]? _encoded;
    private long _position;
    internal bool LargeIds { get; set; }
    internal uint FromItemId { get; set; }
    internal uint[] ToItemIds { get; set; } = Array.Empty<uint>();
    internal byte[] TrailingData { get; set; } = Array.Empty<byte>();
    internal void Read(BmffStream stream, HeifReaderOptions options)
    {
        _position = stream.Offset;
        _encoded = HeifFields.Bytes(stream, stream.Remaining, options.MaxBufferedPayloadSize);
    }
    internal void Decode(HeifReaderOptions options)
    {
        if (_encoded is null)
        {
            return;
        }

        var reader = new BinaryContentReader(_encoded, _position);
        FromItemId = LargeIds ? reader.ReadUInt32BigEndian() : reader.ReadUInt16BigEndian();
        var count = HeifFields.Count(reader.ReadUInt16BigEndian(), options.MaxExtentCount, reader.Remaining, LargeIds ? 4 : 2, reader.Position);
        if ((long)count * 4 > options.MaxBufferedPayloadSize)
        {
            throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, reader.Position);
        }

        ToItemIds = new uint[count];
        for (var i = 0; i < count; i++)
        {
            ToItemIds[i] = LargeIds ? reader.ReadUInt32BigEndian() : reader.ReadUInt16BigEndian();
        }

        TrailingData = reader.UnreadSpan.ToArray();
        _encoded = null;
    }
    internal void Write(BmffStream stream, HeifReaderOptions options)
    {
        Decode(options);
        HeifFields.Write(stream, FromItemId, LargeIds ? 4 : 2);
        HeifFields.Write(stream, (uint)ToItemIds.Length, 2);
        foreach (var id in ToItemIds)
        {
            HeifFields.Write(stream, id, LargeIds ? 4 : 2);
        }

        stream.Write(TrailingData);
    }
    internal long Size(HeifReaderOptions options)
    {
        Decode(options);
        if (ToItemIds.Length > ushort.MaxValue || (!LargeIds && FromItemId > ushort.MaxValue))
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, _position);
        }

        foreach (var id in ToItemIds)
        {
            if (!LargeIds && id > ushort.MaxValue)
            {
                throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, _position);
            }
        }

        return (LargeIds ? 6L : 4L) + ToItemIds.Length * (LargeIds ? 4L : 2L) + TrailingData.Length;
    }
}
