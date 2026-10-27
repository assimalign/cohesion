using System;
using System.IO;
using System.Globalization;
using System.Text;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Documents.Catalog;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentCatalogCodec
{
    internal const byte IndexRecordVersion = 3;
    private static readonly UTF8Encoding _utf8 = new(false, true);

    internal static byte[] Encode(CatalogRecord record, TransactionSequence writer)
    {
        using var stream = new MemoryStream();
        using var binary = new BinaryWriter(stream, _utf8, leaveOpen: true);
        binary.Write(writer.Value);
        binary.Write(0UL);
        binary.Write((byte)(record.Collection is not null ? 1 : record.Document is not null ? 2 : 4));
        binary.Write(record.Index is not null ? IndexRecordVersion : (byte)1);
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
            binary.Write(index.Keys.Count);
            foreach (var key in index.Keys)
            {
                binary.Write((byte)(key.ArraySource is null ? 0 : 1));
                WriteString(binary, key.Path);
                if (key.ArraySource is not null)
                {
                    WriteString(binary, key.ArraySource);
                    DocumentIndexPredicateCodec.Write(binary, key.When);
                }
            }
            binary.Write((byte)((index.IsUnique ? 1 : 0) | (index.IncludeMissing ? 2 : 0)));
            binary.Write(index.ObjectId);
        }
        return stream.ToArray();
    }

    internal static void EnsureHeader(ReadOnlySpan<byte> bytes, Assimalign.Cohesion.Database.Storage.PageId page, int slot)
    {
        try
        {
            if (bytes.Length < 18 || bytes[16] is not (1 or 2 or 4))
            {
                throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogHeaderMalformed);
            }
            EnsureVersion(bytes[16], bytes[17]);
        }
        catch (DocumentCatalogException error) { throw At(page, slot, error); }
    }

    internal static CatalogRecord Decode(ReadOnlySpan<byte> bytes, Assimalign.Cohesion.Database.Storage.PageId page, int slot)
    {
        try { return Decode(bytes); }
        catch (DocumentCatalogException error)
        {
            throw At(page, slot, error);
        }
    }

    internal static DocumentCatalogException At(Assimalign.Cohesion.Database.Storage.PageId page, int slot, DocumentCatalogException error)
    {
        if (error.IsFormatRefusal)
        {
            return new DocumentCatalogException(error.Code, string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.CatalogFormatRefusalLocation, (long)page, slot, error.Message), error)
            { IsFormatRefusal = true, PageId = page, SlotIndex = slot };
        }
        var corruption = new Assimalign.Cohesion.Database.Storage.StorageCorruptionException(page, slot, error.Message, error);
        return new DocumentCatalogException(error.Code, corruption.Message, corruption)
        { PageId = page, SlotIndex = slot };
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
            EnsureVersion(kind, version);
            var id = new Guid(reader.ReadBytes(16));
            string name = ReadString(reader) ?? throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog);
            CatalogRecord result;
            switch (kind)
            {
                case 1:
                    var owner = (DatabaseObjectOwner)reader.ReadByte();
                    string? schema = ReadString(reader);
                    if (owner is not (DatabaseObjectOwner.Adhoc or DatabaseObjectOwner.Schema)
                        || (owner == DatabaseObjectOwner.Schema && string.IsNullOrWhiteSpace(schema)))
                    {
                        throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog2);
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
                        throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog3);
                    }
                    result = new CatalogRecord(null, new DocumentCatalogEntry(id, name, documentVersion, head, length, checksum));
                    break;
                case 4:
                    int count = reader.ReadInt32();
                    if (count is < 1 or > 32) { throw new DocumentCatalogException("CD06105", ErrorMessages.IndexPathCountInvalid); }
                    var keys = new DocumentIndexKey[count];
                    for (int i = 0; i < count; i++)
                    {
                        byte keyKind = reader.ReadByte();
                        string path = ReadString(reader) ?? throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog4);
                        keys[i] = keyKind switch
                        {
                            0 => DocumentIndexKey.Scalar(path),
                            1 => DocumentIndexKey.DistinctArray(ReadString(reader)
                                ?? throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog4),
                                path, DocumentIndexPredicateCodec.Read(reader)),
                            _ => throw new DocumentCatalogException("CD06105", ErrorMessages.IndexDescriptorInvalid)
                        };
                    }
                    byte flags = reader.ReadByte();
                    if (flags > 3) { throw new DocumentCatalogException("CD06105", ErrorMessages.IndexFlagsInvalid); }
                    ulong objectId = reader.ReadUInt64();
                    if (objectId == 0)
                    {
                        throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog5);
                    }
                    if (System.Linq.Enumerable.Count(keys, key => key.ArraySource is not null) > 1 ||
                        (flags & 1) != 0 && System.Linq.Enumerable.Any(keys, key => key.ArraySource is not null))
                    { throw new DocumentCatalogException("CD06105", ErrorMessages.IndexDescriptorInvalid); }
                    result = new CatalogRecord(null, null, new DocumentIndexMetadata(id, name, keys, (flags & 1) != 0, (flags & 2) != 0, objectId));
                    break;
                default:
                    throw new DocumentCatalogException("CD06105", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogCodecDecodeDocumentCatalog6, kind));
            }
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || stream.Position != stream.Length)
            {
                throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog7);
            }
            return result;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
        {
            throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecDecodeDocumentCatalog8, error);
        }
    }

    internal static void EnsureVersion(byte kind, byte version)
    {
        byte supported = kind is 4 or 5 ? IndexRecordVersion : (byte)1;
        if (version != supported)
        {
            throw new DocumentCatalogException("CD06105", string.Format(CultureInfo.CurrentCulture, ErrorMessages.CatalogFormatMismatch, kind, version, supported))
            { IsFormatRefusal = true };
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
            throw new DocumentCatalogException("CD06105", ErrorMessages.CatalogCodecReadStringDocumentCatalog);
        }
        return _utf8.GetString(reader.ReadBytes(length));
    }
}
