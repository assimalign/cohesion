using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

internal static class BlobCatalogCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(CatalogRecord record, TransactionSequence writer)
    {
        using var stream = new MemoryStream();
        using var binary = new BinaryWriter(stream, Utf8, leaveOpen: true);
        binary.Write(writer.Value);
        binary.Write(0UL);
        binary.Write((byte)(record.Container is not null ? 1 : 2));
        binary.Write((byte)1);
        if (record.Container is { } container)
        {
            binary.Write(container.Id.ToByteArray());
            WriteString(binary, container.Name);
            binary.Write((byte)container.Owner);
            WriteString(binary, container.OwningSchema);
        }
        else
        {
            var blob = record.Blob!.Value;
            binary.Write(blob.ContainerId.ToByteArray());
            WriteString(binary, blob.Name);
            binary.Write(blob.Length);
            WriteString(binary, blob.ContentType);
            binary.Write(blob.ETag);
            WriteTime(binary, blob.CreatedAt);
            WriteTime(binary, blob.ModifiedAt);
            binary.Write(blob.Checksum);
            binary.Write(blob.HeadLocation);
        }
        return stream.ToArray();
    }

    internal static CatalogRecord Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8);
            reader.ReadUInt64();
            reader.ReadUInt64();
            byte kind = reader.ReadByte();
            byte version = reader.ReadByte();
            if (version != 1)
            {
                throw new BlobCatalogException($"Unsupported blob catalog format version {version}.");
            }
            byte[] identity = reader.ReadBytes(16);
            if (identity.Length != 16)
            {
                throw new BlobCatalogException("Truncated blob catalog identity.");
            }
            var id = new Guid(identity);
            string name = ReadString(reader) ?? throw new BlobCatalogException("A catalog name cannot be null.");
            CatalogRecord result;
            if (kind == 1)
            {
                var owner = (DatabaseObjectOwner)reader.ReadByte();
                string? schema = ReadString(reader);
                if (owner is not (DatabaseObjectOwner.Adhoc or DatabaseObjectOwner.Schema)
                    || (owner == DatabaseObjectOwner.Schema && string.IsNullOrWhiteSpace(schema)))
                {
                    throw new BlobCatalogException("Invalid container ownership metadata.");
                }
                result = new CatalogRecord(new BlobContainerMetadata(id, name, owner, schema), null);
            }
            else if (kind == 2)
            {
                long length = reader.ReadInt64();
                string? contentType = ReadString(reader);
                ulong etag = reader.ReadUInt64();
                var created = ReadTime(reader);
                var modified = ReadTime(reader);
                uint checksum = reader.ReadUInt32();
                ulong head = reader.ReadUInt64();
                if (length < 0 || ((length == 0) != (head == 0)))
                {
                    throw new BlobCatalogException("Invalid blob content length or head reference.");
                }
                result = new CatalogRecord(null, new BlobCatalogEntry(id, name, length, contentType, etag, created, modified, checksum, head));
            }
            else
            {
                throw new BlobCatalogException($"Unexpected blob metadata record kind {kind}.");
            }
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || stream.Position != stream.Length)
            {
                throw new BlobCatalogException("Invalid or trailing blob catalog metadata.");
            }
            return result;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
        {
            throw new BlobCatalogException("Malformed blob catalog metadata.", error);
        }
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }
        byte[] bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == -1)
        {
            return null;
        }
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new BlobCatalogException("Invalid catalog string length.");
        }
        return Utf8.GetString(reader.ReadBytes(length));
    }

    private static void WriteTime(BinaryWriter writer, DateTimeOffset time)
    {
        writer.Write(time.UtcTicks);
        writer.Write(checked((short)time.Offset.TotalMinutes));
    }

    private static DateTimeOffset ReadTime(BinaryReader reader)
        => new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(reader.ReadInt16()));
}
