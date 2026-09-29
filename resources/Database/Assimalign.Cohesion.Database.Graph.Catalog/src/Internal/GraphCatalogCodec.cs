using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Catalog.Internal;

internal static class GraphCatalogCodec
{
    private static readonly UTF8Encoding _utf8 = new(false, true);

    internal static byte[] Encode(CatalogRecord record, TransactionSequence writer)
    {
        Validate(record);
        using var stream = new MemoryStream();
        using var binary = new BinaryWriter(stream, _utf8, leaveOpen: true);
        binary.Write(writer.Value);
        binary.Write(0UL);
        binary.Write(record.Kind);
        binary.Write((byte)1);
        binary.Write(record.Id.ToByteArray());
        WriteString(binary, record.Name);
        if (record.Kind <= 2)
        {
            binary.Write((byte)record.Owner);
            WriteString(binary, record.OwningSchema);
        }
        else if (record.Property is { } property)
        {
            binary.Write(property.Type is { } type ? (byte)type : byte.MaxValue);
            binary.Write(property.Required ? (byte)1 : (byte)0);
        }
        else
        {
            WriteString(binary, record.Index!.Value.PropertyKey);
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
                throw new GraphCatalogException($"Unsupported graph catalog format version {version}.");
            }
            var id = new Guid(reader.ReadBytes(16));
            string name = ReadString(reader) ?? throw new GraphCatalogException("A catalog name cannot be null.");
            CatalogRecord result;
            if (kind is 1 or 2)
            {
                var owner = (DatabaseObjectOwner)reader.ReadByte();
                string? schema = ReadString(reader);
                result = kind == 1
                    ? new CatalogRecord(Label: new GraphLabelMetadata(id, name, owner, schema))
                    : new CatalogRecord(RelationshipType: new GraphRelationshipTypeMetadata(id, name, owner, schema));
            }
            else if (kind == 3)
            {
                byte type = reader.ReadByte();
                byte required = reader.ReadByte();
                if (required > 1)
                {
                    throw new GraphCatalogException("Invalid required-property marker.");
                }
                result = new CatalogRecord(Property: new GraphPropertyKeyMetadata(id, name,
                    type == byte.MaxValue ? null : (DatabaseType)type, required == 1));
            }
            else if (kind == 4)
            {
                result = new CatalogRecord(Index: new GraphIndexMetadata(id, name,
                    ReadString(reader) ?? throw new GraphCatalogException("An index property cannot be null.")));
            }
            else
            {
                throw new GraphCatalogException($"Unexpected graph metadata record kind {kind}.");
            }
            Validate(result);
            if (stream.Position != stream.Length)
            {
                throw new GraphCatalogException("Trailing graph catalog metadata.");
            }
            return result;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
        {
            throw new GraphCatalogException("Malformed graph catalog metadata.", error);
        }
    }

    internal static void Validate(CatalogRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Name);
        if (record.Id == Guid.Empty)
        {
            throw new ArgumentException("A graph definition requires a nonempty identity.");
        }
        if (record.Kind <= 2 && (record.Owner is not (DatabaseObjectOwner.Adhoc or DatabaseObjectOwner.Schema)
            || (record.Owner == DatabaseObjectOwner.Schema && string.IsNullOrWhiteSpace(record.OwningSchema))
            || (record.Owner == DatabaseObjectOwner.Adhoc && record.OwningSchema is not null)))
        {
            throw new ArgumentException("Only a schema-owned definition may name an owning schema, and must name one.");
        }
        if (record.Property?.Type is { } type && (byte)type > (byte)DatabaseType.JsonBinary)
        {
            throw new ArgumentException("Unknown graph property type.");
        }
        if (record.Index is { } index)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(index.PropertyKey);
        }
    }

    private static void WriteString(BinaryWriter writer, string? value)
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

    private static string? ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == -1)
        {
            return null;
        }
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new GraphCatalogException("Invalid graph catalog string length.");
        }
        return _utf8.GetString(reader.ReadBytes(length));
    }
}

