using System;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// Bridges boxed runtime values and the self-describing key codec: appends a value
/// to a <see cref="DatabaseKeyWriter"/> by its runtime type, and reads the next
/// component of a <see cref="DatabaseKeyReader"/> back as a boxed value.
/// </summary>
/// <remarks>
/// This is the codec for boundaries where values arrive untyped — the wire
/// protocol's parameters and result rows, where the server decodes what a client
/// encoded without either side holding a schema. Strings encode under
/// <see cref="Collation.Binary"/> (exact round-trip; ordering is irrelevant at
/// these boundaries). Supported runtime types: <c>bool</c>, <c>sbyte</c>,
/// <c>short</c>, <c>int</c>, <c>long</c>, <c>float</c>, <c>double</c>,
/// <c>decimal</c>, <c>string</c>, <c>byte[]</c>, <c>DateOnly</c>, <c>TimeOnly</c>,
/// <c>DateTime</c>, <c>DateTimeOffset</c>, <c>TimeSpan</c>, <c>Guid</c>, and null.
/// </remarks>
public static class DatabaseValueCodec
{
    /// <summary>
    /// Appends a boxed value to the writer as one self-describing component,
    /// dispatching on the value's runtime type.
    /// </summary>
    /// <param name="writer">The writer to append to.</param>
    /// <param name="value">The value to append; null appends a null component.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is null.</exception>
    /// <exception cref="DatabaseTypeException">Thrown when the value's runtime type has no component encoding.</exception>
    public static void Append(DatabaseKeyWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (value)
        {
            case null: writer.AppendNull(); break;
            case bool typed: writer.AppendBoolean(typed); break;
            case sbyte typed: writer.AppendInt8(typed); break;
            case short typed: writer.AppendInt16(typed); break;
            case int typed: writer.AppendInt32(typed); break;
            case long typed: writer.AppendInt64(typed); break;
            case float typed: writer.AppendFloat32(typed); break;
            case double typed: writer.AppendFloat64(typed); break;
            case decimal typed: writer.AppendDecimal(typed); break;
            case string typed: writer.AppendString(typed, Collation.Binary); break;
            case byte[] typed: writer.AppendBinary(typed); break;
            case DateOnly typed: writer.AppendDate(typed); break;
            case TimeOnly typed: writer.AppendTime(typed); break;
            case DateTime typed: writer.AppendDateTime(typed); break;
            case DateTimeOffset typed: writer.AppendDateTimeOffset(typed); break;
            case TimeSpan typed: writer.AppendTimeSpan(typed); break;
            case Guid typed: writer.AppendGuid(typed); break;
            default:
                throw new DatabaseTypeException($"Values of runtime type {value.GetType()} have no component encoding.");
        }
    }

    /// <summary>
    /// Reads the next component from the reader as a boxed value.
    /// </summary>
    /// <param name="reader">The reader to consume from.</param>
    /// <returns>The decoded value; null for a null component.</returns>
    /// <exception cref="DatabaseTypeException">Thrown when the next component is malformed or not a scalar value component.</exception>
    public static object? Read(ref DatabaseKeyReader reader)
    {
        return reader.PeekType() switch
        {
            DatabaseType.Null => reader.ReadNull(),
            DatabaseType.Boolean => reader.ReadBoolean(),
            DatabaseType.Int8 => reader.ReadInt8(),
            DatabaseType.Int16 => reader.ReadInt16(),
            DatabaseType.Int32 => reader.ReadInt32(),
            DatabaseType.Int64 => reader.ReadInt64(),
            DatabaseType.Float32 => reader.ReadFloat32(),
            DatabaseType.Float64 => reader.ReadFloat64(),
            DatabaseType.Decimal => reader.ReadDecimal(),
            DatabaseType.String => reader.ReadString(out _),
            DatabaseType.Binary => reader.ReadBinary(),
            DatabaseType.Date => reader.ReadDate(),
            DatabaseType.Time => reader.ReadTime(),
            DatabaseType.DateTime => reader.ReadDateTime(),
            DatabaseType.DateTimeOffset => reader.ReadDateTimeOffset(),
            DatabaseType.TimeSpan => reader.ReadTimeSpan(),
            DatabaseType.Guid => reader.ReadGuid(),
            var other => throw new DatabaseTypeException($"Component type {other} is not a scalar value component."),
        };
    }

    /// <summary>
    /// Encodes one boxed value as a standalone single-component payload (the wire
    /// protocol's parameter encoding).
    /// </summary>
    /// <param name="value">The value to encode; null encodes a null component.</param>
    /// <returns>The encoded component bytes.</returns>
    /// <exception cref="DatabaseTypeException">Thrown when the value's runtime type has no component encoding.</exception>
    public static byte[] EncodeComponent(object? value)
    {
        var writer = new DatabaseKeyWriter(32);
        Append(writer, value);
        return writer.ToArray();
    }

    /// <summary>
    /// Decodes a standalone single-component payload back to a boxed value.
    /// </summary>
    /// <param name="payload">The encoded component bytes.</param>
    /// <returns>The decoded value; null for a null component.</returns>
    /// <exception cref="DatabaseTypeException">Thrown when the payload is malformed, empty, or carries more than one component.</exception>
    public static object? DecodeComponent(ReadOnlySpan<byte> payload)
    {
        var reader = new DatabaseKeyReader(payload);
        object? value = Read(ref reader);

        if (!reader.IsAtEnd)
        {
            throw new DatabaseTypeException("The payload carries more than one component.");
        }

        return value;
    }
}
