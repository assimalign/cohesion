using System;
using System.Globalization;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Graph.Internal;

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
        binary.Write((byte)2);
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
        else if (record.Index is { } index)
        {
            WriteString(binary, index.PropertyKey);
        }
        else
        {
            WriteString(binary, record.Constraint!.Value.PropertyKey);
            binary.Write(record.Constraint.Value.Type is { } type ? (byte)type : byte.MaxValue);
        }
        if (stream.Length > SlottedPage.MaxRecordSize)
        {
            throw new GraphElementTooLargeException(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.CatalogRecordTooLarge, stream.Length, SlottedPage.MaxRecordSize));
        }
        return stream.ToArray();
    }

    internal static CatalogRecord Decode(ReadOnlySpan<byte> bytes, Assimalign.Cohesion.Database.Storage.PageId page, int slot)
    {
        try { return Decode(bytes); }
        catch (GraphCatalogException error) when (error.Diagnostics[0].Code != "CG06019")
        {
            var corruption = new Assimalign.Cohesion.Database.Storage.StorageCorruptionException(page, slot, error.Message, error);
            throw new GraphCatalogException("CG06006", corruption.Message, corruption) { IsConflict = error.IsConflict, IsCallerError = error.IsCallerError };
        }
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
            if (version != 2)
            {
                throw new GraphCatalogException("CG06019", string.Format(CultureInfo.CurrentCulture, ErrorMessages.CatalogFormatUnsupported, version));
            }
            var id = new Guid(reader.ReadBytes(16));
            string name = ReadString(reader) ?? throw new GraphCatalogException("CG06006", ErrorMessages.GraphCatalogCodecDecodeGraphCatalog2);
            CatalogRecord result;
            if (kind is 1 or 2)
            {
                var owner = (DatabaseObjectOwner)reader.ReadByte();
                string? schema = ReadString(reader);
                result = kind == 1
                    ? new CatalogRecord(label: new GraphLabelMetadata(id, name, owner, schema))
                    : new CatalogRecord(relationshipType: new GraphRelationshipTypeMetadata(id, name, owner, schema));
            }
            else if (kind == 3)
            {
                byte type = reader.ReadByte();
                byte required = reader.ReadByte();
                if (required > 1)
                {
                    throw new GraphCatalogException("CG06006", ErrorMessages.GraphCatalogCodecDecodeGraphCatalog3);
                }
                result = new CatalogRecord(property: new GraphPropertyKeyMetadata(id, name,
                    type == byte.MaxValue ? null : (DatabaseType)type, required == 1));
            }
            else if (kind == 4)
            {
                result = new CatalogRecord(index: new GraphIndexMetadata(id, name,
                    ReadString(reader) ?? throw new GraphCatalogException("CG06006", ErrorMessages.GraphCatalogCodecDecodeGraphCatalog4)));
            }
            else if (kind == 5)
            {
                string property = ReadString(reader) ?? throw new GraphCatalogException("CG06006", ErrorMessages.GraphCatalogCodecDecodeGraphCatalog4);
                byte type = reader.ReadByte();
                result = new CatalogRecord(constraint: new GraphConstraintMetadata(id, name, property,
                    type == byte.MaxValue ? null : (DatabaseType)type));
            }
            else
            {
                throw new GraphCatalogException("CG06006", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphCatalogCodecDecodeGraphCatalog5, kind));
            }
            Validate(result);
            if (stream.Position != stream.Length)
            {
                throw new GraphCatalogException("CG06006", ErrorMessages.GraphCatalogCodecDecodeGraphCatalog6);
            }
            return result;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
        {
            throw new GraphCatalogException("CG06006", ErrorMessages.GraphCatalogCodecDecodeGraphCatalog7, error);
        }
    }

    internal static void Validate(CatalogRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Name);
        if (record.Id == Guid.Empty)
        {
            throw new ArgumentException(ErrorMessages.GraphCatalogCodecValidateArgument);
        }
        if (record.Kind <= 2 && (record.Owner is not (DatabaseObjectOwner.Adhoc or DatabaseObjectOwner.Schema)
            || (record.Owner == DatabaseObjectOwner.Schema && string.IsNullOrWhiteSpace(record.OwningSchema))
            || (record.Owner == DatabaseObjectOwner.Adhoc && record.OwningSchema is not null)))
        {
            throw new ArgumentException(ErrorMessages.GraphCatalogCodecValidateArgument2);
        }
        if (record.Property?.Type is { } type && (byte)type > (byte)DatabaseType.JsonBinary)
        {
            throw new ArgumentException(ErrorMessages.GraphCatalogCodecValidateArgument3);
        }
        if (record.Index is { } index)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(index.PropertyKey);
        }
        if (record.Constraint is { } constraint)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(constraint.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(constraint.PropertyKey);
            if (constraint.Type is { } constraintType && (byte)constraintType > (byte)DatabaseType.JsonBinary)
            {
                throw new ArgumentException(ErrorMessages.GraphCatalogCodecValidateArgument3);
            }
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
            throw new GraphCatalogException("CG06006", ErrorMessages.CatalogCodecReadStringGraphCatalog);
        }
        return _utf8.GetString(reader.ReadBytes(length));
    }
}

