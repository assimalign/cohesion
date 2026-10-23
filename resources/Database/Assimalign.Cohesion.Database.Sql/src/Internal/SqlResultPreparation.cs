using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Encodes individual result frames. Writing statements retain their payloads inside execution,
/// including the DML apply bracket; read-only statements encode one frame at a time during delivery.
/// </summary>
internal sealed class SqlResultPreparation
{
    private readonly Func<DatabaseResult>? _resultOverride;
    private DatabaseResult? _prepared;
    private DatabaseKeyWriter _rowWriter = new();
    private int _rowCapacity;

    internal SqlResultPreparation(Func<DatabaseResult>? resultOverride = null)
    {
        _resultOverride = resultOverride;
    }

    internal byte[]? Header { get; private set; }
    internal List<byte[]> Rows { get; } = [];
    internal byte[]? Completion { get; private set; }
    internal bool IsPrepared => _prepared is not null;

    internal async ValueTask<DatabaseResult> OverrideAsync(DatabaseResult result)
    {
        if (_resultOverride is null) { return result; }
        if (result is DatabaseResultSet original) { await original.DisposeAsync().ConfigureAwait(false); }
        return _resultOverride();
    }

    internal async ValueTask<DatabaseResult> PrepareAsync(DatabaseResult result, CancellationToken cancellationToken)
    {
        // DML already prepared its result before ApplyStatementAsync released the undo bracket.
        if (ReferenceEquals(result, _prepared)) { return result; }
        // The owning-assembly seam can throw an execution error of its own.
        result = await OverrideAsync(result).ConfigureAwait(false);
        try
        {
            if (result is DatabaseResultSet resultSet)
            {
                await using (resultSet.ConfigureAwait(false))
                {
                    Header = EncodeHeader(resultSet, cancellationToken);
                    await foreach (DatabaseRow row in resultSet.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Rows.Add(EncodeRow(row, cancellationToken));
                    }
                    Completion = new ProtocolResultCompleteMessage(resultSet.AffectedCount).Encode();
                }
            }
            else { Completion = new ProtocolResultCompleteMessage(result.AffectedCount).Encode(); }
            // Disposal is part of preparation, so a failed release still rolls back the statement.
            _prepared = result;
            return result;
        }
        catch (Exception exception) when (exception is DatabaseException or DatabaseTypeException)
        {
            var failure = TranslateFailure(exception);
            if (ReferenceEquals(failure, exception)) { throw; }
            throw failure;
        }
    }

    internal byte[] EncodeHeader(DatabaseResultSet resultSet, CancellationToken cancellationToken)
    {
        var columns = new List<(string Name, byte Type, int? MaxLength)>(resultSet.Columns.Count);
        long size = 8; // Format marker and column count (SQL header format 2).
        foreach (DatabaseColumn column in resultSet.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            size += 9L + Encoding.UTF8.GetByteCount(column.Name);
            columns.Add((column.Name, (byte)column.Type, column.MaxLength));
        }
        CheckFrameSize(size, ErrorMessages.ResultHeaderTooLarge);
        return new ProtocolResultHeaderMessage(columns).Encode();
    }

    internal byte[] EncodeRow(DatabaseRow row, CancellationToken cancellationToken)
    {
        long size = MeasureRow(row, cancellationToken);
        CheckFrameSize(size, ErrorMessages.ResultRowTooLarge);
        if (size > _rowCapacity)
        {
            _rowCapacity = (int)size;
            _rowWriter = new DatabaseKeyWriter(_rowCapacity);
        }
        _rowWriter.Reset();
        for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseValueCodec.Append(_rowWriter, row.GetValue(ordinal));
        }
        // Keep refusals ahead of delivery even if a future codec changes its size.
        CheckFrameSize(_rowWriter.Length, ErrorMessages.ResultRowTooLarge);
        return _rowWriter.ToArray();
    }

    internal static DatabaseException TranslateFailure(Exception exception)
        => exception is DatabaseException coded &&
            coded.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error && !string.IsNullOrEmpty(item.Code))
                ? coded : Failure(ErrorMessages.ResultDeliveryFailed, exception);

    private static long MeasureRow(DatabaseRow row, CancellationToken cancellationToken)
    {
        long size = 0;
        for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? value = row.GetValue(ordinal);
            // Binary collation's UTF-8 bytes escape each zero and terminate with two zeros.
            // Count variable payloads without allocating their sort keys or copying binary data.
            if (value is string text)
            {
                size += 4L + Encoding.UTF8.GetByteCount(text) + text.AsSpan().Count('\0');
            }
            else if (value is byte[] bytes)
            {
                size += 3L + bytes.Length + bytes.AsSpan().Count((byte)0);
            }
            else
            {
                // Tag plus fixed payload, matching DatabaseKeyWriter without encoding twice.
                size += value switch
                {
                    null => 1,
                    bool or sbyte => 2,
                    short => 3,
                    int or float or DateOnly => 5,
                    long or double or TimeOnly or TimeSpan => 9,
                    DateTime => 10,
                    DateTimeOffset => 11,
                    Guid => 17,
                    decimal number => DecimalSize(number),
                    _ => UnsupportedValueSize(value),
                };
            }
        }
        return size;
    }

    private static int DecimalSize(decimal value)
    {
        if (value == 0) { return 2; } // Tag and zero sign marker.
        // KeyComponentEncoding.WriteDecimal retains only significant digits, plus tag, sign,
        // exponent and terminator. Formatting into a bounded span avoids allocating a payload.
        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out int length, provider: CultureInfo.InvariantCulture);
        int digits = 0, trailingZeros = 0;
        foreach (char character in text[..length])
        {
            if (character is < '0' or > '9') { continue; }
            if (character == '0')
            {
                if (digits > 0) { digits++; trailingZeros++; }
            }
            else { digits++; trailingZeros = 0; }
        }
        return 4 + digits - trailingZeros;
    }

    private static int UnsupportedValueSize(object value)
    {
        // Use the codec's own refusal and message for an unsupported runtime type.
        var writer = new DatabaseKeyWriter();
        DatabaseValueCodec.Append(writer, value);
        return writer.Length;
    }

    private static void CheckFrameSize(long size, string message)
    {
        if (size > ProtocolFrameHeader.MaxPayloadLength)
        {
            throw Failure(string.Format(CultureInfo.CurrentCulture, message, size, ProtocolFrameHeader.MaxPayloadLength));
        }
    }

    private static DatabaseException Failure(string message, Exception? cause = null)
        => new(message, [new Diagnostic { Code = "CS05045", Message = message, Severity = DiagnosticSeverity.Error }], cause);
}
