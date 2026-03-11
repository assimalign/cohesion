using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a model-agnostic tuple (named fields) that can be persisted as a storage record.
/// </summary>
/// <remarks>
/// This type provides a shared tuple wire format for row-oriented use cases while still allowing
/// model-specific encodings (JSON, BSON, binary row layouts) when needed.
/// </remarks>
public readonly struct StorageTuple
{
    private readonly StorageTupleField[] _fields;

    /// <summary>
    /// Initializes a new <see cref="StorageTuple"/> from fields.
    /// </summary>
    /// <param name="fields">The tuple fields.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="fields"/> is null.</exception>
    public StorageTuple(params StorageTupleField[] fields)
    {
        _fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }

    /// <summary>
    /// Gets the number of fields in the tuple.
    /// </summary>
    public int Count => _fields?.Length ?? 0;

    /// <summary>
    /// Gets the field at the specified zero-based index.
    /// </summary>
    /// <param name="index">Field index.</param>
    /// <returns>The field at <paramref name="index"/>.</returns>
    public StorageTupleField this[int index] => _fields[index];

    /// <summary>
    /// Attempts to get a tuple field by name (ordinal comparison).
    /// </summary>
    /// <param name="name">The field name to find.</param>
    /// <param name="field">When this method returns true, contains the matching field.</param>
    /// <returns>True if a field with the specified name exists; otherwise false.</returns>
    public bool TryGetField(string name, out StorageTupleField field)
    {
        if (_fields is not null)
        {
            for (int i = 0; i < _fields.Length; i++)
            {
                if (string.Equals(_fields[i].Name, name, StringComparison.Ordinal))
                {
                    field = _fields[i];
                    return true;
                }
            }
        }

        field = default;
        return false;
    }

    /// <summary>
    /// Serializes this tuple into a compact binary representation.
    /// </summary>
    /// <returns>Serialized tuple bytes.</returns>
    public byte[] ToBytes()
    {
        if (_fields is null || _fields.Length == 0)
        {
            return BitConverter.GetBytes(0);
        }

        int totalSize = sizeof(int);
        for (int i = 0; i < _fields.Length; i++)
        {
            int nameLen = Encoding.UTF8.GetByteCount(_fields[i].Name);
            totalSize += sizeof(int) + nameLen;
            totalSize += sizeof(int) + _fields[i].Value.Length;
        }

        byte[] buffer = new byte[totalSize];
        var span = buffer.AsSpan();
        int offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), _fields.Length);
        offset += sizeof(int);

        for (int i = 0; i < _fields.Length; i++)
        {
            string name = _fields[i].Name;
            ReadOnlyMemory<byte> value = _fields[i].Value;

            int nameLen = Encoding.UTF8.GetByteCount(name);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), nameLen);
            offset += sizeof(int);

            Encoding.UTF8.GetBytes(name.AsSpan(), span.Slice(offset, nameLen));
            offset += nameLen;

            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), value.Length);
            offset += sizeof(int);

            value.Span.CopyTo(span.Slice(offset, value.Length));
            offset += value.Length;
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes a tuple from bytes previously produced by <see cref="ToBytes"/>.
    /// </summary>
    /// <param name="data">Tuple bytes.</param>
    /// <returns>The deserialized tuple.</returns>
    /// <exception cref="ArgumentException">Thrown when data is malformed.</exception>
    public static StorageTuple FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < sizeof(int))
        {
            throw new ArgumentException("Tuple payload is too small.", nameof(data));
        }

        int offset = 0;
        int fieldCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
        offset += sizeof(int);

        if (fieldCount < 0)
        {
            throw new ArgumentException("Tuple field count cannot be negative.", nameof(data));
        }

        var fields = new StorageTupleField[fieldCount];

        for (int i = 0; i < fieldCount; i++)
        {
            if (offset + sizeof(int) > data.Length)
            {
                throw new ArgumentException("Tuple payload is truncated while reading field name length.", nameof(data));
            }

            int nameLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
            offset += sizeof(int);

            if (nameLen <= 0 || offset + nameLen > data.Length)
            {
                throw new ArgumentException("Tuple payload contains an invalid field name length.", nameof(data));
            }

            string name = Encoding.UTF8.GetString(data.Slice(offset, nameLen));
            offset += nameLen;

            if (offset + sizeof(int) > data.Length)
            {
                throw new ArgumentException("Tuple payload is truncated while reading field value length.", nameof(data));
            }

            int valueLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
            offset += sizeof(int);

            if (valueLen < 0 || offset + valueLen > data.Length)
            {
                throw new ArgumentException("Tuple payload contains an invalid field value length.", nameof(data));
            }

            byte[] value = data.Slice(offset, valueLen).ToArray();
            offset += valueLen;

            fields[i] = new StorageTupleField(name, value);
        }

        if (offset != data.Length)
        {
            throw new ArgumentException("Tuple payload contains trailing bytes.", nameof(data));
        }

        return new StorageTuple(fields);
    }

    /// <summary>
    /// Enumerates all tuple fields.
    /// </summary>
    /// <returns>A read-only view of the tuple fields.</returns>
    public IReadOnlyList<StorageTupleField> AsReadOnlyList()
    {
        return _fields ?? Array.Empty<StorageTupleField>();
    }
}
