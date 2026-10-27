using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphRecordCodec
{
    // One graph format fence, shared by open and recovery; #1633 requires type postings.
    internal const byte FormatVersion = 5;
    internal static void EnsureFormat(ReadOnlySpan<byte> bytes, PageId? page = null, int slot = 0)
    {
        if (bytes.Length < 18)
        { throw Corrupt(page, slot, ErrorMessages.RecordTruncated, new EndOfStreamException(ErrorMessages.RecordTruncated)); }
        if (bytes[17] != FormatVersion)
        { throw UnsupportedFormat(page, slot); }
    }

    internal static StorageCorruptionException UnsupportedFormat(PageId? page = null, int slot = 0)
    {
        string message = ErrorMessages.RecordFormatUnsupported;
        var format = new GraphRecordFormatException(message);
        return Corrupt(page, slot, message, format);
    }

    internal static byte[] Encode(GraphRecord record, TransactionSequence writer)
    {
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        output.Write(writer.Value);
        output.Write(0UL);
        output.Write(record.Kind);
        output.Write(FormatVersion);
        output.Write(record.Id);
        output.Write(record.Revision);
        if (record.Node is { } node)
        {
            output.Write(node.Labels.Count);
            for (int i = 0; i < node.Labels.Count; i++)
            { output.Write(node.Labels[i]); output.Write(record.LabelIds![i].ToByteArray()); }
            Properties(output, node.Properties);
        }
        else if (record.Relationship is { } relationship)
        {
            output.Write(relationship.SourceId);
            output.Write(relationship.TargetId);
            output.Write(record.TypeId.ToByteArray());
            output.Write(relationship.Type);
            Properties(output, relationship.Properties);
        }
        else
        {
            output.Write(record.Label!);
            output.Write(record.PropertyKey!);
        }
        if (stream.Length > SlottedPage.MaxRecordSize)
        {
            // One record holds the whole element, so its size is the user's input, not a store
            // fault: a typed failure lets the engine fail the statement and keep the session.
            string element = record.Kind switch
            {
                1 => "A node's labels and properties",
                2 => "A relationship's type and properties",
                _ => "A property index definition's label and property key",
            };
            throw new GraphElementTooLargeException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphRecordCodecEncodeFailure, element, stream.Length, SlottedPage.MaxRecordSize));
        }
        return stream.ToArray();
    }

    internal static GraphRecord Decode(ReadOnlyMemory<byte> bytes, PageId? page = null, int slot = 0)
    {
        EnsureFormat(bytes.Span, page, slot);
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), false);
            using var input = new BinaryReader(stream, new UTF8Encoding(false, true));
            input.ReadUInt64(); input.ReadUInt64();
            byte kind = input.ReadByte();
            if (input.ReadByte() != FormatVersion) { throw UnsupportedFormat(page, slot); }
            ulong id = input.ReadUInt64();
            ulong revision = input.ReadUInt64();
            GraphRecord record;
            switch (kind)
            {
                case 1:
                    int count = Count(input, page, slot);
                    var labels = new string[count];
                    var labelIds = new Guid[count];
                    for (int i = 0; i < count; i++)
                    {
                        labels[i] = input.ReadString();
                        labelIds[i] = new Guid(input.ReadBytes(16));
                        if (labelIds[i] == Guid.Empty) { throw Corrupt(page, slot, ErrorMessages.LabelIdInvalid); }
                    }
                    record = new GraphRecord(kind, id, new StoredGraphNode(id, Array.AsReadOnly(labels), Properties(input, page, slot)), LabelIds: labelIds);
                    break;
                case 2:
                    ulong source = input.ReadUInt64(), target = input.ReadUInt64();
                    var typeId = new Guid(input.ReadBytes(16));
                    if (typeId == Guid.Empty) { throw Corrupt(page, slot, ErrorMessages.RelationshipTypeIdInvalid); }
                    record = new GraphRecord(kind, id, Relationship: new StoredGraphRelationship(id,
                        source, target, input.ReadString(), Properties(input, page, slot)), TypeId: typeId);
                    break;
                case 3: record = new GraphRecord(kind, id, Label: input.ReadString(), PropertyKey: input.ReadString()); break;
                default: throw Corrupt(page, slot, ErrorMessages.RecordKindInvalid);
            }
            if (id == 0 || stream.Position != stream.Length) { throw Corrupt(page, slot, ErrorMessages.RecordPayloadInvalid); }
            return record with { Revision = revision };
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException or FormatException or ArgumentException or OverflowException)
        {
            throw Corrupt(page, slot, string.Format(CultureInfo.CurrentCulture, ErrorMessages.RecordDecodeFailed, error.Message), error);
        }
    }

    private static StorageCorruptionException Corrupt(PageId? page, int slot, string message, Exception? inner = null)
        => page is { } id ? new StorageCorruptionException(id, slot, message, inner)
            : new StorageCorruptionException(message);

    private static int Count(BinaryReader input, PageId? page, int slot)
    {
        int count = input.ReadInt32();
        if (count < 0 || count > SlottedPage.MaxRecordSize) { throw Corrupt(page, slot, ErrorMessages.RecordCountInvalid); }
        return count;
    }

    private static void Properties(BinaryWriter output, IReadOnlyDictionary<string, object?> properties)
    {
        output.Write(properties.Count);
        foreach (var property in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(property.Key);
            output.Write(property.Key);
            switch (property.Value)
            {
                case null: output.Write((byte)0); break;
                case bool value: output.Write((byte)1); output.Write(value); break;
                case string value: output.Write((byte)2); output.Write(value); break;
                case sbyte value: output.Write((byte)3); output.Write(value); break;
                case byte value: output.Write((byte)4); output.Write(value); break;
                case short value: output.Write((byte)5); output.Write(value); break;
                case ushort value: output.Write((byte)6); output.Write(value); break;
                case int value: output.Write((byte)7); output.Write(value); break;
                case uint value: output.Write((byte)8); output.Write(value); break;
                case long value: output.Write((byte)9); output.Write(value); break;
                case ulong value: output.Write((byte)10); output.Write(value); break;
                case decimal value: output.Write((byte)11); output.Write(value); break;
                case double value when double.IsFinite(value): output.Write((byte)12); output.Write(value); break;
                case float value when float.IsFinite(value): output.Write((byte)13); output.Write(value); break;
                default: throw new ArgumentException(ErrorMessages.GraphRecordCodecPropertiesArgument);
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> Properties(BinaryReader input, PageId? page, int slot)
    {
        int count = Count(input, page, slot);
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = input.ReadString();
            object? value = input.ReadByte() switch
            {
                0 => null, 1 => input.ReadBoolean(), 2 => input.ReadString(), 3 => input.ReadSByte(), 4 => input.ReadByte(),
                5 => input.ReadInt16(), 6 => input.ReadUInt16(), 7 => input.ReadInt32(), 8 => input.ReadUInt32(),
                9 => input.ReadInt64(), 10 => input.ReadUInt64(), 11 => input.ReadDecimal(), 12 => input.ReadDouble(), 13 => input.ReadSingle(),
                _ => throw Corrupt(page, slot, ErrorMessages.RecordScalarTagInvalid)
            };
            if (value is double number && !double.IsFinite(number) || value is float single && !float.IsFinite(single))
            {
                throw Corrupt(page, slot, ErrorMessages.RecordScalarNotFinite);
            }
            properties.Add(key, value);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(properties);
    }

    /// <summary>The longest scalar key prefix, leaving the 8-byte node identity within the 1,024-byte B+Tree key.</summary>
    internal const int MaximumKeyPrefix = 1016;

    /// <summary>The index key prefix of a stored property value.</summary>
    /// <param name="value">The property value.</param>
    /// <returns>The key prefix.</returns>
    /// <exception cref="ArgumentException">The value is not a supported index scalar.</exception>
    /// <exception cref="GraphElementTooLargeException">The value encodes past <see cref="MaximumKeyPrefix"/> bytes.</exception>
    internal static IndexKey Key(object? value) => TryKey(value, out var key) ? key
        : throw new GraphElementTooLargeException(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphRecordCodecKeyFailure, key.Length, MaximumKeyPrefix));

    /// <summary>
    /// The index key prefix of a property value, or <see langword="false"/> when it encodes past
    /// <see cref="MaximumKeyPrefix"/> bytes. No stored entry holds such a key, so a search for it
    /// matches nothing.
    /// </summary>
    /// <param name="value">The property value.</param>
    /// <param name="key">The key prefix, oversized when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the key fits.</returns>
    /// <exception cref="ArgumentException">The value is not a supported index scalar.</exception>
    internal static bool TryKey(object? value, out IndexKey key)
    {
        var writer = new DatabaseKeyWriter();
        key = value switch
        {
            null => IndexKey.From(writer.AppendNull()),
            bool boolean => IndexKey.From(writer.AppendBoolean(boolean)),
            string text => IndexKey.From(writer.AppendBinary(Encoding.BigEndianUnicode.GetBytes(text))),
            decimal or byte or sbyte or short or ushort or int or uint or long or ulong or double or float
                => IndexKey.From(writer.AppendFloat64(NumberKey(value))),
            _ => throw new ArgumentException(ErrorMessages.GraphRecordCodecTryKeyArgument, nameof(value))
        };
        return key.Length <= MaximumKeyPrefix;
    }

    // Float64 is a candidate projection: large integers/nearby decimals may share a key.
    // Equality is checked against the original scalar before materializing a result.
    private static double NumberKey(object value)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) { throw new ArgumentException(ErrorMessages.GraphRecordCodecNumberKeyArgument, nameof(value)); }
        return number == 0 ? 0d : number;
    }

    internal static bool ScalarEquals(object? left, object? right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            return left is float or double || right is float or double
                ? Convert.ToDouble(left, CultureInfo.InvariantCulture).Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture))
                : Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture);
        }
        return Equals(left, right);
    }

    private static bool IsNumber(object? value) => value is decimal or byte or sbyte or short or ushort or int or uint or long or ulong or double or float;
}
