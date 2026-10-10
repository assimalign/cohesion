using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class HPackDecoder
{
    public const int DefaultHeaderTableSize = 4096;

    /// <summary>
    /// RFC 9113 §10.5.1 — each decoded field contributes its name length plus its value length plus
    /// 32 octets of overhead to the header-list size accounting.
    /// </summary>
    private const int HeaderListFieldOverhead = 32;

    private readonly int _maxDynamicTableSize;
    private readonly long _maxHeaderListSize;
    private readonly HPackDynamicTable _dynamicTable;
    private long _currentHeaderListSize;

    public HPackDecoder(int maxDynamicTableSize = DefaultHeaderTableSize, long maxHeaderListSize = long.MaxValue)
    {
        _maxDynamicTableSize = maxDynamicTableSize;
        _maxHeaderListSize = maxHeaderListSize;
        _dynamicTable = new HPackDynamicTable(maxDynamicTableSize);
    }

    /// <summary>
    /// Decodes a request head and folds its field lines into an <see cref="HPackDecodedHeaders"/>. The
    /// whole block is decoded before any field is judged, so a block that cannot be decompressed is
    /// always reported as such (RFC 9113 §4.3), even when it also carries a field that breaks a rule.
    /// </summary>
    /// <param name="headerBlock">The complete field block (a HEADERS payload plus its CONTINUATION payloads).</param>
    /// <returns>The decoded request head.</returns>
    /// <exception cref="HPackDecodingException">The block is not a valid HPACK encoding.</exception>
    /// <exception cref="HPackHeaderListSizeExceededException">
    /// The decoded list exceeds the advertised <c>SETTINGS_MAX_HEADER_LIST_SIZE</c>; the decode stops there.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// The block decodes, but a field breaks an RFC 9113 §8.2 / §8.3 field rule (see <see cref="HPackDecodedHeaders.Add"/>).
    /// </exception>
    public HPackDecodedHeaders DecodeRequestHeaders(ReadOnlySpan<byte> headerBlock)
    {
        List<(string Name, string Value)> fieldLines = DecodeFieldLines(headerBlock);
        HPackDecodedHeaders decodedHeaders = new();

        foreach ((string name, string value) in fieldLines)
        {
            decodedHeaders.Add(name, value);
        }

        decodedHeaders.Complete();
        return decodedHeaders;
    }

    /// <summary>
    /// Decodes a field block into its field lines, in wire order, without applying any field rule.
    /// Used for a request head (<see cref="DecodeRequestHeaders"/>), for a trailer section, and for a
    /// block that is decoded only to keep the dynamic table in step: the caller judges the fields
    /// once the whole block has been processed, so a field the caller rejects never leaves part of
    /// the block undecoded.
    /// </summary>
    /// <param name="headerBlock">The complete field block (a HEADERS payload plus its CONTINUATION payloads).</param>
    /// <returns>The decoded field lines.</returns>
    /// <exception cref="HPackDecodingException">The block is not a valid HPACK encoding.</exception>
    /// <exception cref="HPackHeaderListSizeExceededException">
    /// The decoded list exceeds the advertised <c>SETTINGS_MAX_HEADER_LIST_SIZE</c>; the decode stops there.
    /// </exception>
    public List<(string Name, string Value)> DecodeFieldLines(ReadOnlySpan<byte> headerBlock)
    {
        List<(string Name, string Value)> fieldLines = new();
        DecodeFieldBlock(headerBlock, (name, value) => fieldLines.Add((name, value)));
        return fieldLines;
    }

    private void DecodeFieldBlock(ReadOnlySpan<byte> headerBlock, Action<string, string> addField)
    {
        int index = 0;
        bool fieldLineDecoded = false;

        // RFC 9113 §10.5.1 — the header-list size is accounted per field section, so reset the
        // running total for every decode. The dynamic-table state is intentionally connection-wide
        // and is NOT reset here.
        _currentHeaderListSize = 0;

        while (index < headerBlock.Length)
        {
            byte current = headerBlock[index];

            if ((current & 0x20) != 0 && (current & 0xC0) == 0)
            {
                // RFC 7541 §4.2 — a dynamic table size update opens a field block: one that follows
                // a field line is a decoding error.
                if (fieldLineDecoded)
                {
                    throw new HPackDecodingException(
                        "An HPACK dynamic table size update followed a field line; it must come at the beginning of the field block (RFC 7541 §4.2).");
                }

                int dynamicTableSize = DecodeInteger(headerBlock, ref index, 5);
                ResizeDynamicTable(dynamicTableSize);
                continue;
            }

            fieldLineDecoded = true;

            if ((current & 0x80) != 0)
            {
                int headerIndex = DecodeInteger(headerBlock, ref index, 7);
                ref readonly HPackHeaderField headerField = ref GetHeaderField(headerIndex);
                AccountAndAdd(addField, ToAsciiString(headerField.Name), ToAsciiString(headerField.Value));
                continue;
            }

            if ((current & 0x40) != 0)
            {
                DecodeLiteralHeaderField(headerBlock, ref index, 6, addField, indexHeader: true);
                continue;
            }

            DecodeLiteralHeaderField(headerBlock, ref index, 4, addField, indexHeader: false);
        }
    }

    private void DecodeLiteralHeaderField(ReadOnlySpan<byte> headerBlock, ref int index, int prefixLength, Action<string, string> addField, bool indexHeader)
    {
        int nameIndex = DecodeInteger(headerBlock, ref index, prefixLength);
        string name;
        byte[]? nameBytesBuffer = null;
        int? staticNameIndex = null;

        if (nameIndex == 0)
        {
            nameBytesBuffer = DecodeStringBytes(headerBlock, ref index);
            ReadOnlySpan<byte> nameBytes = nameBytesBuffer;
            name = ToAsciiString(nameBytes);
        }
        else
        {
            ref readonly HPackHeaderField headerField = ref GetHeaderField(nameIndex);
            ReadOnlySpan<byte> nameBytes = headerField.Name;
            name = ToAsciiString(nameBytes);

            if (nameIndex <= HPackStaticTable.Count)
            {
                staticNameIndex = nameIndex;
            }
        }

        byte[] valueBytesBuffer = DecodeStringBytes(headerBlock, ref index);
        ReadOnlySpan<byte> valueBytes = valueBytesBuffer;
        string value = ToAsciiString(valueBytes);
        AccountAndAdd(addField, name, value);

        if (!indexHeader)
        {
            return;
        }

        if (staticNameIndex.HasValue)
        {
            _dynamicTable.Insert(staticNameIndex.Value, nameIndex == 0 ? nameBytesBuffer : GetHeaderField(nameIndex).Name, valueBytes);
        }
        else
        {
            _dynamicTable.Insert(nameIndex == 0 ? nameBytesBuffer : GetHeaderField(nameIndex).Name, valueBytes);
        }
    }

    private void AccountAndAdd(Action<string, string> addField, string name, string value)
    {
        // RFC 9113 §10.5.1 — bound the decoded header list by the advertised
        // SETTINGS_MAX_HEADER_LIST_SIZE. Accounting each field as name + value + 32 octets and
        // aborting the moment the running total exceeds the cap stops HPACK amplification (a small
        // encoded block of indexed references that expands into a huge decoded list) before the
        // large list is ever materialised.
        _currentHeaderListSize += (long)name.Length + value.Length + HeaderListFieldOverhead;

        if (_currentHeaderListSize > _maxHeaderListSize)
        {
            throw new HPackHeaderListSizeExceededException(
                $"The decoded HTTP/2 header list exceeded the advertised SETTINGS_MAX_HEADER_LIST_SIZE of {_maxHeaderListSize} octets.");
        }

        addField(name, value);
    }

    private ref readonly HPackHeaderField GetHeaderField(int index)
    {
        if (index <= 0)
        {
            throw new HPackDecodingException("The HPACK header index must be greater than zero.");
        }

        if (index <= HPackStaticTable.Count)
        {
            return ref HPackStaticTable.Get(index - 1);
        }

        int dynamicIndex = index - HPackStaticTable.Count - 1;

        if ((uint)dynamicIndex >= _dynamicTable.Count)
        {
            throw new HPackDecodingException($"The HPACK header index '{index}' was outside the dynamic table range.");
        }

        return ref _dynamicTable[dynamicIndex];
    }

    private void ResizeDynamicTable(int size)
    {
        if (size > _maxDynamicTableSize)
        {
            throw new HPackDecodingException($"The HPACK dynamic table size '{size}' exceeded the configured maximum of '{_maxDynamicTableSize}'.");
        }

        _dynamicTable.Resize(size);
    }

    private static byte[] DecodeStringBytes(ReadOnlySpan<byte> headerBlock, ref int index)
    {
        if (index >= headerBlock.Length)
        {
            throw new HPackDecodingException("The HPACK string literal was incomplete.");
        }

        // RFC 7541 §5.2 — the high bit ("H") of the first octet indicates
        // whether the string is Huffman-encoded. The remaining 7 bits
        // (extended with continuation octets when ≥ 127) encode the
        // length in octets on the wire.
        bool huffmanEncoded = (headerBlock[index] & 0x80) != 0;
        int length = DecodeInteger(headerBlock, ref index, 7);

        // Compared against what is left, not as index + length: a length near 2^31 would overflow
        // the sum and slip past the check.
        if (length > headerBlock.Length - index)
        {
            throw new HPackDecodingException("The HPACK string literal length exceeded the available payload.");
        }

        ReadOnlySpan<byte> raw = headerBlock.Slice(index, length);
        index += length;

        return huffmanEncoded
            ? HPackHuffmanDecoder.Decode(raw)
            : raw.ToArray();
    }

    private static int DecodeInteger(ReadOnlySpan<byte> buffer, ref int index, int prefixLength)
    {
        if (index >= buffer.Length)
        {
            throw new HPackDecodingException("The HPACK integer was incomplete.");
        }

        IntegerDecoder integerDecoder = new();
        byte first = (byte)(buffer[index++] & ((1 << prefixLength) - 1));

        if (integerDecoder.BeginTryDecode(first, prefixLength, out int value))
        {
            return value;
        }

        while (index < buffer.Length)
        {
            if (integerDecoder.TryDecode(buffer[index++], out value))
            {
                return value;
            }
        }

        throw new HPackDecodingException("The HPACK integer was incomplete.");
    }

    private static string ToAsciiString(ReadOnlySpan<byte> value)
    {
        return value.IsEmpty ? string.Empty : Encoding.ASCII.GetString(value);
    }
}
