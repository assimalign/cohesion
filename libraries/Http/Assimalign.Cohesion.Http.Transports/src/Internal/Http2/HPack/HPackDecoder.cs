using System;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;

internal sealed class HPackDecoder
{
    public const int DefaultHeaderTableSize = 4096;

    private readonly int _maxDynamicTableSize;
    private readonly HPackDynamicTable _dynamicTable;

    public HPackDecoder(int maxDynamicTableSize = DefaultHeaderTableSize)
    {
        _maxDynamicTableSize = maxDynamicTableSize;
        _dynamicTable = new HPackDynamicTable(maxDynamicTableSize);
    }

    public HPackDecodedHeaders DecodeRequestHeaders(ReadOnlySpan<byte> headerBlock)
    {
        HPackDecodedHeaders decodedHeaders = new();
        int index = 0;

        while (index < headerBlock.Length)
        {
            byte current = headerBlock[index];

            if ((current & 0x80) != 0)
            {
                int headerIndex = DecodeInteger(headerBlock, ref index, 7);
                ref readonly HPackHeaderField headerField = ref GetHeaderField(headerIndex);
                decodedHeaders.Add(ToAsciiString(headerField.Name), ToAsciiString(headerField.Value));
                continue;
            }

            if ((current & 0x40) != 0)
            {
                DecodeLiteralHeaderField(headerBlock, ref index, 6, decodedHeaders, indexHeader: true);
                continue;
            }

            if ((current & 0x20) != 0)
            {
                int dynamicTableSize = DecodeInteger(headerBlock, ref index, 5);
                ResizeDynamicTable(dynamicTableSize);
                continue;
            }

            DecodeLiteralHeaderField(headerBlock, ref index, 4, decodedHeaders, indexHeader: false);
        }

        return decodedHeaders;
    }

    private void DecodeLiteralHeaderField(ReadOnlySpan<byte> headerBlock, ref int index, int prefixLength, HPackDecodedHeaders decodedHeaders, bool indexHeader)
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
        decodedHeaders.Add(name, value);

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

        if ((headerBlock[index] & 0x80) != 0)
        {
            throw new HPackDecodingException("Huffman encoded HPACK strings are not currently supported.");
        }

        int length = DecodeInteger(headerBlock, ref index, 7);

        if (index + length > headerBlock.Length)
        {
            throw new HPackDecodingException("The HPACK string literal length exceeded the available payload.");
        }

        ReadOnlySpan<byte> value = headerBlock.Slice(index, length);
        index += length;
        return value.ToArray();
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
