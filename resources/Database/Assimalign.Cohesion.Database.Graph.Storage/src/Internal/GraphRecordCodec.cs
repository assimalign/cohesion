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

namespace Assimalign.Cohesion.Database.Graph.Storage.Internal;

internal readonly record struct GraphRecord(byte Kind, ulong Id, StoredGraphNode? Node = null,
    StoredGraphRelationship? Relationship = null, string? Label = null, string? PropertyKey = null);

internal static class GraphRecordCodec
{
    internal static byte[] Encode(GraphRecord record, TransactionSequence writer)
    {
        using var stream = new MemoryStream();
        using var output = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        output.Write(writer.Value);
        output.Write(0UL);
        output.Write(record.Kind);
        output.Write((byte)1);
        output.Write(record.Id);
        if (record.Node is { } node)
        {
            output.Write(node.Labels.Count);
            foreach (string label in node.Labels) { output.Write(label); }
            Properties(output, node.Properties);
        }
        else if (record.Relationship is { } relationship)
        {
            output.Write(relationship.SourceId);
            output.Write(relationship.TargetId);
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
            throw new ArgumentException($"Graph record exceeds the {SlottedPage.MaxRecordSize}-byte slotted-record limit.");
        }
        return stream.ToArray();
    }

    internal static GraphRecord Decode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), false);
            using var input = new BinaryReader(stream, new UTF8Encoding(false, true));
            input.ReadUInt64(); input.ReadUInt64();
            byte kind = input.ReadByte();
            if (input.ReadByte() != 1) { throw new StorageCorruptionException("Unknown graph record version."); }
            ulong id = input.ReadUInt64();
            GraphRecord record;
            switch (kind)
            {
                case 1:
                    int count = Count(input);
                    var labels = new string[count];
                    for (int i = 0; i < count; i++) { labels[i] = input.ReadString(); }
                    record = new GraphRecord(kind, id, new StoredGraphNode(id, Array.AsReadOnly(labels), Properties(input)));
                    break;
                case 2:
                    record = new GraphRecord(kind, id, Relationship: new StoredGraphRelationship(id,
                        input.ReadUInt64(), input.ReadUInt64(), input.ReadString(), Properties(input)));
                    break;
                case 3: record = new GraphRecord(kind, id, Label: input.ReadString(), PropertyKey: input.ReadString()); break;
                default: throw new StorageCorruptionException("Unknown graph record kind.");
            }
            if (id == 0 || stream.Position != stream.Length) { throw new StorageCorruptionException("Invalid graph record identity or trailing payload."); }
            return record;
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException or FormatException or ArgumentException or OverflowException)
        {
            throw new StorageCorruptionException($"Invalid graph record: {error.Message}");
        }
    }

    private static int Count(BinaryReader input)
    {
        int count = input.ReadInt32();
        if (count < 0 || count > SlottedPage.MaxRecordSize) { throw new StorageCorruptionException("Invalid graph property count."); }
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
                default: throw new ArgumentException("Graph properties support null, Boolean, String, Decimal, integral types, and finite floating point values.");
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> Properties(BinaryReader input)
    {
        int count = Count(input);
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = input.ReadString();
            object? value = input.ReadByte() switch
            {
                0 => null, 1 => input.ReadBoolean(), 2 => input.ReadString(), 3 => input.ReadSByte(), 4 => input.ReadByte(),
                5 => input.ReadInt16(), 6 => input.ReadUInt16(), 7 => input.ReadInt32(), 8 => input.ReadUInt32(),
                9 => input.ReadInt64(), 10 => input.ReadUInt64(), 11 => input.ReadDecimal(), 12 => input.ReadDouble(), 13 => input.ReadSingle(),
                _ => throw new StorageCorruptionException("Unknown graph property scalar tag.")
            };
            if (value is double number && !double.IsFinite(number) || value is float single && !float.IsFinite(single))
            {
                throw new StorageCorruptionException("Graph numeric properties must be finite.");
            }
            properties.Add(key, value);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(properties);
    }

    internal static IndexKey Key(object? value)
    {
        var writer = new DatabaseKeyWriter();
        var key = value switch
        {
            null => IndexKey.From(writer.AppendNull()),
            bool boolean => IndexKey.From(writer.AppendBoolean(boolean)),
            string text => IndexKey.From(writer.AppendBinary(Encoding.BigEndianUnicode.GetBytes(text))),
            decimal or byte or sbyte or short or ushort or int or uint or long or ulong or double or float
                => IndexKey.From(writer.AppendFloat64(NumberKey(value))),
            _ => throw new ArgumentException("Unsupported graph property index scalar.", nameof(value))
        };
        if (key.Length > 1016) { throw new ArgumentException("Graph property index scalar exceeds 1016 bytes before its 8-byte node identity suffix.", nameof(value)); }
        return key;
    }

    // Float64 is a candidate projection: large integers/nearby decimals may share a key.
    // Equality is checked against the original scalar before materializing a result.
    private static double NumberKey(object value)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) { throw new ArgumentException("Graph index bounds require finite numeric values.", nameof(value)); }
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
