using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3.QPack;

/// <summary>
/// Decodes a QPACK encoded field section (RFC 9204 §4.5) into its ordered
/// name/value field lines for the supported feature set: the QPACK dynamic
/// table is disabled (<c>QPACK_MAX_TABLE_CAPACITY = 0</c>), so every field
/// line resolves against the static table or carries a literal name. Any
/// representation that would require the dynamic table — a non-zero
/// Required Insert Count, a dynamic indexed/name reference, or a post-base
/// reference — is rejected as a decompression failure (RFC 9204 §2.2).
/// </summary>
internal static class QPackFieldSectionDecoder
{
    /// <summary>
    /// Decodes the field section into its field lines, in wire order.
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

        List<(string Name, string Value)> fields = [];

        while (index < buffer.Length)
        {
            byte first = buffer[index];

            if ((first & 0x80) != 0)
            {
                // §4.5.2 Indexed Field Line: 1 T xxxxxx. T = static(1)/dynamic(0).
                bool isStatic = (first & 0x40) != 0;
                long staticIndex = QPackPrefixedInteger.Decode(buffer, ref index, 6);

                if (!isStatic)
                {
                    throw new InvalidDataException("QPACK dynamic-table indexed field line is not supported (dynamic table disabled).");
                }

                if (!QPackStaticTable.TryGet((int)staticIndex, out string name, out string value))
                {
                    throw new InvalidDataException($"QPACK static-table index {staticIndex} is out of range.");
                }

                fields.Add((name, value));
            }
            else if ((first & 0x40) != 0)
            {
                // §4.5.4 Literal Field Line with Name Reference: 0 1 N T xxxx.
                bool isStatic = (first & 0x10) != 0;
                long nameIndex = QPackPrefixedInteger.Decode(buffer, ref index, 4);

                if (!isStatic)
                {
                    throw new InvalidDataException("QPACK dynamic-table name reference is not supported (dynamic table disabled).");
                }

                if (!QPackStaticTable.TryGet((int)nameIndex, out string name, out _))
                {
                    throw new InvalidDataException($"QPACK static-table name index {nameIndex} is out of range.");
                }

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
            else
            {
                // §4.5.3 Indexed Field Line with Post-Base Index (0001xxxx)
                // and §4.5.5 Literal Field Line with Post-Base Name Reference
                // (0000Nxxx) both reference the dynamic table.
                throw new InvalidDataException("QPACK post-base field line is not supported (dynamic table disabled).");
            }
        }

        return fields;
    }
}
