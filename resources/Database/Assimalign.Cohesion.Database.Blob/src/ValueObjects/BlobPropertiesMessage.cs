using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Carries one object's complete catalog properties.</summary>
/// <param name="Properties">The object's properties, with timestamps normalized to UTC.</param>
public readonly record struct BlobPropertiesMessage(BlobProperties Properties)
{
    /// <summary>Encodes one object's properties.</summary>
    /// <returns>The metadata payload.</returns>
    /// <exception cref="ProtocolException">The object name, content type or length is invalid.</exception>
    public byte[] Encode()
    {
        if (Properties.Length < 0)
        {
            throw new ProtocolException("A Blob property length must be nonnegative.");
        }
        var buffer = new List<byte>();
        BlobProtocolPayload.WriteString(buffer, Properties.Name, allowEmpty: false);
        BlobProtocolPayload.WriteInt64(buffer, Properties.Length);
        buffer.Add(Properties.ContentType is null ? (byte)0 : (byte)1);
        if (Properties.ContentType is not null)
        {
            BlobProtocolPayload.WriteString(buffer, Properties.ContentType, allowEmpty: true);
        }
        BlobProtocolPayload.WriteInt64(buffer, unchecked((long)Properties.ETag));
        BlobProtocolPayload.WriteInt64(buffer, Properties.CreatedAt.UtcTicks);
        BlobProtocolPayload.WriteInt64(buffer, Properties.ModifiedAt.UtcTicks);
        Span<byte> checksum = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, Properties.Checksum);
        buffer.AddRange(checksum.ToArray());
        return buffer.ToArray();
    }

    /// <summary>Decodes one object's properties.</summary>
    /// <param name="payload">The complete metadata payload.</param>
    /// <returns>The decoded properties.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobPropertiesMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string name = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: false);
        long length = BlobProtocolPayload.ReadInt64(payload, ref position);
        if (length < 0 || position >= payload.Length || payload[position] > 1)
        {
            throw new ProtocolException("Malformed Blob property length or content-type flag.");
        }
        string? contentType = payload[position++] == 0 ? null : BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: true);
        ulong etag = unchecked((ulong)BlobProtocolPayload.ReadInt64(payload, ref position));
        long created = BlobProtocolPayload.ReadInt64(payload, ref position);
        long modified = BlobProtocolPayload.ReadInt64(payload, ref position);
        if (created < DateTime.MinValue.Ticks || created > DateTime.MaxValue.Ticks ||
            modified < DateTime.MinValue.Ticks || modified > DateTime.MaxValue.Ticks || payload.Length - position != sizeof(uint))
        {
            throw new ProtocolException("Malformed Blob property timestamps or checksum.");
        }
        uint checksum = BinaryPrimitives.ReadUInt32BigEndian(payload[position..]);
        return new(new(name, length, contentType, etag, new DateTimeOffset(created, TimeSpan.Zero),
            new DateTimeOffset(modified, TimeSpan.Zero), checksum));
    }
}
