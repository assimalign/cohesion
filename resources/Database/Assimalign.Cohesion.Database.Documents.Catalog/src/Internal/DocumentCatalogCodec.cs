using System;
using System.IO;
using System.Text;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Internal;

internal static class DocumentCatalogCodec
{
    private static readonly UTF8Encoding _utf8 = new(false, true);

    internal static byte[] Encode(CatalogRecord record, TransactionSequence writer)
    {
        using var stream = new MemoryStream();
        using var binary = new BinaryWriter(stream, _utf8, leaveOpen: true);
        binary.Write(writer.Value);
        binary.Write(0UL);
        binary.Write((byte)(record.Collection is not null ? 1 : record.Document is not null ? 2 : 4));
        binary.Write((byte)1);
        if (record.Collection is { } collection)
        {
            binary.Write(collection.Id.ToByteArray());
            WriteString(binary, collection.Name);
            binary.Write((byte)collection.Owner);
            WriteString(binary, collection.OwningSchema);
        }
        else if (record.Document is { } document)
        {
            binary.Write(document.CollectionId.ToByteArray());
            WriteString(binary, document.Id);
            binary.Write(document.Version);
            binary.Write(document.HeadLocation);
            binary.Write(document.Length);
            binary.Write(document.Checksum);
        }
        else
        {
            var index = record.Index!.Value;
            binary.Write(index.CollectionId.ToByteArray());
            WriteString(binary, index.Name);
            WriteString(binary, index.Path);
            binary.Write(index.ObjectId);
        }
        return stream.ToArray();
    }

    internal static CatalogRecord Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, _utf8);
            reader.ReadUInt64();
            reader.ReadUInt64();
            byte kind = reader.ReadByte();
            byte version = reader.ReadByte();
            if (version != 1)
            {
                throw new DocumentCatalogException($"Unsupported document catalog format version {version}.");
            }
            var id = new Guid(reader.ReadBytes(16));
            string name = ReadString(reader) ?? throw new DocumentCatalogException("Catalog names cannot be null.");
            CatalogRecord result;
            switch (kind)
            {
                case 1:
                    var owner = (DatabaseObjectOwner)reader.ReadByte();
                    string? schema = ReadString(reader);
                    if (owner is not (DatabaseObjectOwner.Adhoc or DatabaseObjectOwner.Schema)
                        || (owner == DatabaseObjectOwner.Schema && string.IsNullOrWhiteSpace(schema)))
                    {
                        throw new DocumentCatalogException("Invalid collection ownership metadata.");
                    }
                    result = new CatalogRecord(new DocumentCollectionMetadata(id, name, owner, schema), null);
                    break;
                case 2:
                    ulong documentVersion = reader.ReadUInt64();
                    ulong head = reader.ReadUInt64();
                    long length = reader.ReadInt64();
                    uint checksum = reader.ReadUInt32();
                    if (documentVersion == 0 || head == 0 || length <= 0 || length > int.MaxValue)
                    {
                        throw new DocumentCatalogException("Invalid document version or content reference.");
                    }
                    result = new CatalogRecord(null, new DocumentCatalogEntry(id, name, documentVersion, head, length, checksum));
                    break;
                case 4:
                    string path = ReadString(reader) ?? throw new DocumentCatalogException("Index paths cannot be null.");
                    ulong objectId = reader.ReadUInt64();
                    DocumentIndexKeys.ValidatePath(path);
                    if (objectId == 0)
                    {
                        throw new DocumentCatalogException("Index identities cannot be zero.");
                    }
                    result = new CatalogRecord(null, null, new DocumentIndexMetadata(id, name, path, objectId));
                    break;
                default:
                    throw new DocumentCatalogException($"Unexpected document metadata record kind {kind}.");
            }
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || stream.Position != stream.Length)
            {
                throw new DocumentCatalogException("Invalid or trailing document catalog metadata.");
            }
            return result;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
        {
            throw new DocumentCatalogException("Malformed document catalog metadata.", error);
        }
    }

    internal static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }
        byte[] bytes = _utf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static string? ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == -1)
        {
            return null;
        }
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new DocumentCatalogException("Invalid catalog string length.");
        }
        return _utf8.GetString(reader.ReadBytes(length));
    }
}
