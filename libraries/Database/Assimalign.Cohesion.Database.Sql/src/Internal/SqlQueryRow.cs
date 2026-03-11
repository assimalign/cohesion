using System;
using System.Text;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Wraps raw storage bytes as a single-field query row.
/// </summary>
/// <remarks>
/// In the initial schema-less implementation, each row is treated as a single binary field
/// (ordinal 0). Typed accessors interpret the raw bytes at offset 0. When the T-SQL parser
/// matures and table schemas are introduced, this will be replaced with column-aware decoding.
/// </remarks>
internal sealed class SqlQueryRow : QueryRow
{
    private readonly ReadOnlyMemory<byte> _data;

    internal SqlQueryRow(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    /// <inheritdoc />
    public override int FieldCount => 1;

    /// <inheritdoc />
    public override bool IsNull(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _data.IsEmpty;
    }

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetBytes(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _data;
    }

    /// <inheritdoc />
    public override string? GetString(int ordinal)
    {
        ValidateOrdinal(ordinal);

        if (_data.IsEmpty)
        {
            return null;
        }

        return Encoding.UTF8.GetString(_data.Span);
    }

    /// <inheritdoc />
    public override int GetInt32(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return BitConverter.ToInt32(_data.Span);
    }

    /// <inheritdoc />
    public override long GetInt64(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return BitConverter.ToInt64(_data.Span);
    }

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return BitConverter.ToBoolean(_data.Span);
    }

    /// <inheritdoc />
    public override double GetDouble(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return BitConverter.ToDouble(_data.Span);
    }

    /// <inheritdoc />
    public override object? GetValue(int ordinal)
    {
        ValidateOrdinal(ordinal);

        if (_data.IsEmpty)
        {
            return null;
        }

        return _data.ToArray();
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Only ordinal 0 is supported in schema-less mode.");
        }
    }
}
