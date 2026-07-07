using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// Decodes a QPACK encoded field section (RFC 9204 §4.5) into its ordered
/// name/value field lines. Two modes are supported behind the shared
/// field-line walk:
/// </summary>
/// <remarks>
/// <para>
/// <b>Static-only</b> (<see cref="Decode(ReadOnlySpan{byte})"/>) is the default
/// posture when <c>QPACK_MAX_TABLE_CAPACITY = 0</c>: every field line resolves
/// against the static table or carries a literal, and any representation that
/// would require the dynamic table — a non-zero Required Insert Count, a dynamic
/// indexed/name reference, or a post-base reference — is rejected as a
/// per-stream parse failure (the request stream is dropped; the connection
/// survives).
/// </para>
/// <para>
/// <b>Dynamic</b> (<see cref="Decode(ReadOnlySpan{byte}, QPackDynamicTable)"/>)
/// resolves dynamic indexed, dynamic name-reference, and post-base references
/// against the supplied dynamic table using the reconstructed Base. A reference
/// that cannot be resolved is a connection error
/// (<c>QPACK_DECOMPRESSION_FAILED</c>, RFC 9204 §2.2) because the dynamic table
/// state is shared across streams.
/// </para>
/// </remarks>
internal static class QPackFieldSectionDecoder
{
    /// <summary>
    /// Decodes a field section in static-only mode (dynamic table disabled).
    /// </summary>
    /// <param name="buffer">The encoded field section (prefix + field lines).</param>
    /// <returns>The decoded name/value pairs in order.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the section is malformed or uses a representation that
    /// requires the (disabled) dynamic table.
    /// </exception>
    public static List<(string Name, string Value)> Decode(ReadOnlySpan<byte> buffer)
    {
        int index = 0;

        // RFC 9204 §4.5.1 — Field Section Prefix. With the dynamic table
        // disabled the peer MUST encode Required Insert Count = 0; any other
        // value means it referenced dynamic entries we never allowed.
        long requiredInsertCount = QPackPrefixedInteger.Decode(buffer, ref index, 8);
        if (requiredInsertCount != 0)
        {
            throw new InvalidDataException(
                "QPACK Required Insert Count must be 0 — the dynamic table is disabled (QPACK_MAX_TABLE_CAPACITY = 0).");
        }

        // Delta Base (S flag + Base). With Required Insert Count 0 there is
        // no base; decode to advance past it and ignore the value.
        _ = QPackPrefixedInteger.Decode(buffer, ref index, 7);

        return DecodeFieldLines(buffer, index, table: null, baseIndex: 0);
    }

    /// <summary>
    /// Decodes a field section in dynamic mode, resolving dynamic and post-base
    /// references against <paramref name="table"/>.
    /// </summary>
    /// <param name="buffer">The encoded field section (prefix + field lines).</param>
    /// <param name="table">
    /// The dynamic table. It must already hold at least Required Insert Count
    /// insertions (the caller blocks until it does, RFC 9204 §2.1.2).
    /// </param>
    /// <returns>The decoded name/value pairs in order.</returns>
    /// <exception cref="QPackException">
    /// Thrown (<c>QPACK_DECOMPRESSION_FAILED</c>) when the Required Insert Count
    /// is unsatisfiable or a reference cannot be resolved.
    /// </exception>
    /// <exception cref="InvalidDataException">Thrown when the section is truncated.</exception>
    public static List<(string Name, string Value)> Decode(ReadOnlySpan<byte> buffer, QPackDynamicTable table)
    {
        QPackFieldSectionPrefix prefix = QPackFieldSectionPrefix.Parse(buffer, table.MaxCapacity, table.InsertCount);

        if (prefix.RequiredInsertCount > table.InsertCount)
        {
            // The caller is expected to block until the insertions arrive; if it
            // did not, the section is unsatisfiable (RFC 9204 §2.2).
            throw new QPackException(
                Http3ErrorCode.QPackDecompressionFailed,
                $"The QPACK field section requires insert count {prefix.RequiredInsertCount} but only {table.InsertCount} entries have been inserted.");
        }

        return DecodeFieldLines(buffer, prefix.BodyOffset, table, prefix.Base);
    }

    /// <summary>
    /// Reads the field section prefix and reconstructs the Required Insert Count
    /// so the caller can decide whether the stream must block (RFC 9204 §2.1.2)
    /// before a full decode.
    /// </summary>
    /// <param name="buffer">The encoded field section.</param>
    /// <param name="table">The dynamic table (supplies capacity and insert count).</param>
    /// <returns>The reconstructed absolute Required Insert Count.</returns>
    public static long PeekRequiredInsertCount(ReadOnlySpan<byte> buffer, QPackDynamicTable table)
        => QPackFieldSectionPrefix.Parse(buffer, table.MaxCapacity, table.InsertCount).RequiredInsertCount;

    /// <summary>
    /// Decodes the field lines using an already-parsed prefix. Splitting the
    /// prefix parse from the body decode lets a caller reconstruct the Required
    /// Insert Count and Base once — before blocking on pending insertions — and
    /// then decode against the fixed Base, avoiding a Base that drifts as more
    /// insertions arrive during the wait.
    /// </summary>
    /// <param name="buffer">The encoded field section.</param>
    /// <param name="table">The dynamic table entries are resolved against.</param>
    /// <param name="prefix">The prefix parsed from <paramref name="buffer"/>.</param>
    /// <returns>The decoded name/value pairs in order.</returns>
    /// <exception cref="QPackException">Thrown when a reference cannot be resolved.</exception>
    /// <exception cref="InvalidDataException">Thrown when the section is truncated.</exception>
    public static List<(string Name, string Value)> DecodeBody(ReadOnlySpan<byte> buffer, QPackDynamicTable table, QPackFieldSectionPrefix prefix)
        => DecodeFieldLines(buffer, prefix.BodyOffset, table, prefix.Base);

    private static List<(string Name, string Value)> DecodeFieldLines(ReadOnlySpan<byte> buffer, int index, QPackDynamicTable? table, long baseIndex)
    {
        List<(string Name, string Value)> fields = [];

        while (index < buffer.Length)
        {
            byte first = buffer[index];

            if ((first & 0x80) != 0)
            {
                // §4.5.2 Indexed Field Line: 1 T xxxxxx. T = static(1)/dynamic(0).
                bool isStatic = (first & 0x40) != 0;
                long fieldIndex = QPackPrefixedInteger.Decode(buffer, ref index, 6);

                if (isStatic)
                {
                    fields.Add(ResolveStaticField(fieldIndex));
                }
                else
                {
                    long absolute = baseIndex - 1 - fieldIndex;
                    fields.Add(ResolveDynamicField(table, absolute));
                }
            }
            else if ((first & 0x40) != 0)
            {
                // §4.5.4 Literal Field Line with Name Reference: 0 1 N T xxxx.
                bool isStatic = (first & 0x10) != 0;
                long nameIndex = QPackPrefixedInteger.Decode(buffer, ref index, 4);
                string name = isStatic
                    ? ResolveStaticName(nameIndex)
                    : ResolveDynamicName(table, baseIndex - 1 - nameIndex);

                string value = QPackStringCodec.Decode(buffer, ref index, 7);
                fields.Add((name, value));
            }
            else if ((first & 0x20) != 0)
            {
                // §4.5.6 Literal Field Line with Literal Name: 0 0 1 N H xxx.
                string name = QPackStringCodec.Decode(buffer, ref index, 3);
                string value = QPackStringCodec.Decode(buffer, ref index, 7);
                fields.Add((name, value));
            }
            else if ((first & 0x10) != 0)
            {
                // §4.5.3 Indexed Field Line with Post-Base Index: 0 0 0 1 xxxx.
                long postBaseIndex = QPackPrefixedInteger.Decode(buffer, ref index, 4);
                fields.Add(ResolveDynamicField(table, baseIndex + postBaseIndex));
            }
            else
            {
                // §4.5.5 Literal Field Line with Post-Base Name Reference: 0 0 0 0 N xxx.
                long postBaseIndex = QPackPrefixedInteger.Decode(buffer, ref index, 3);
                string name = ResolveDynamicName(table, baseIndex + postBaseIndex);
                string value = QPackStringCodec.Decode(buffer, ref index, 7);
                fields.Add((name, value));
            }
        }

        return fields;
    }

    private static (string Name, string Value) ResolveStaticField(long staticIndex)
    {
        if (!QPackStaticTable.TryGet((int)staticIndex, out string name, out string value))
        {
            throw new InvalidDataException($"QPACK static-table index {staticIndex} is out of range.");
        }

        return (name, value);
    }

    private static string ResolveStaticName(long nameIndex)
    {
        if (!QPackStaticTable.TryGet((int)nameIndex, out string name, out _))
        {
            throw new InvalidDataException($"QPACK static-table name index {nameIndex} is out of range.");
        }

        return name;
    }

    private static (string Name, string Value) ResolveDynamicField(QPackDynamicTable? table, long absoluteIndex)
    {
        if (table is null)
        {
            throw new InvalidDataException("QPACK dynamic-table reference is not supported (dynamic table disabled).");
        }

        if (!table.TryGetByAbsoluteIndex(absoluteIndex, out string name, out string value))
        {
            throw new QPackException(
                Http3ErrorCode.QPackDecompressionFailed,
                $"QPACK field line references dynamic entry {absoluteIndex}, which is not available.");
        }

        return (name, value);
    }

    private static string ResolveDynamicName(QPackDynamicTable? table, long absoluteIndex)
        => ResolveDynamicField(table, absoluteIndex).Name;
}
