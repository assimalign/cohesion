using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal static class PdfFilters
{
    internal static byte[] Decode(PdfStream stream, PdfReaderOptions options, Func<IPdfObject, IPdfObject>? resolve)
    {
        var data = stream.Data; // Encryption is checked even when there are no filters.
        var position = stream.Position;
        if (stream.Dictionary.Get("F") is not (null or PdfNull))
        {
            throw new ContentFormatException(ErrorMessages.FilterUnsupported, ContentFormatErrorCode.Unsupported, position);
        }
        IPdfObject? Resolve(IPdfObject? value) => value is null ? null : resolve?.Invoke(value) ?? value;
        var filter = Resolve(stream.Dictionary.Get("Filter"));
        var parameters = Resolve(stream.Dictionary.Get("DecodeParms"));
        var filters = filter as PdfArray;
        if (filter is not (null or PdfNull or PdfName or PdfArray))
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        var count = filters?.Count ?? (filter is PdfName ? 1 : 0);
        if (count > options.MaxDepth)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position);
        }
        if (parameters is PdfArray parameterArray && parameterArray.Count != count)
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        if (parameters is not (null or PdfNull or PdfDictionary or PdfArray) || (count > 1 && parameters is PdfDictionary))
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        // Reject unsupported codecs before any payload materialization.
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            if (Resolve(filters is null ? filter : filters[i]) is not PdfName name)
            {
                throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidStructure, position);
            }
            var text = Encoding.ASCII.GetString(name.Bytes.Span);
            if (options.IsLenient)
            {
                text = text switch { "Fl" => "FlateDecode", "AHx" => "ASCIIHexDecode", "A85" => "ASCII85Decode", "LZW" => "LZWDecode", "RL" => "RunLengthDecode", _ => text };
            }
            if (text is not ("FlateDecode" or "ASCIIHexDecode" or "ASCII85Decode" or "LZWDecode" or "RunLengthDecode"))
            {
                throw new ContentFormatException(ErrorMessages.FilterUnsupported, ContentFormatErrorCode.Unsupported, position);
            }
            names[i] = text;
        }
        var materializationLimit = count == 0 ? Math.Min(options.MaxBufferedBytes, options.MaxDecodedBytes) : options.MaxBufferedBytes;
        if (data.Length > materializationLimit)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position);
        }
        var bytes = data.ToArray(materializationLimit);
        for (var i = 0; i < count; i++)
        {
            var parameter = Resolve(parameters is PdfArray array ? array[i] : parameters);
            if (parameter is not (null or PdfNull or PdfDictionary))
            {
                throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidStructure, position);
            }
            var dictionary = parameter as PdfDictionary;
            bytes = names[i] switch
            {
                "FlateDecode" => Flate(bytes, options.MaxDecodedBytes, position),
                "ASCIIHexDecode" => AsciiHex(bytes, options.MaxDecodedBytes, position),
                "ASCII85Decode" => Ascii85(bytes, options.MaxDecodedBytes, position),
                "RunLengthDecode" => RunLength(bytes, options.MaxDecodedBytes, position),
                _ => Lzw(bytes, Parameter(dictionary, "EarlyChange", 1, resolve, position), options.MaxDecodedBytes, position)
            };
            if (names[i] is "FlateDecode" or "LZWDecode")
            {
                bytes = Predict(bytes, dictionary, options.MaxDecodedBytes, resolve, position);
            }
        }
        return bytes;
    }
    private static byte[] Flate(byte[] input, int limit, long position)
    {
        if (input.Length < 6) { throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position); }
        if ((input[0] & 15) != 8 || input[0] >> 4 > 7 || ((input[0] << 8) | input[1]) % 31 != 0 || (input[1] & 32) != 0)
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position);
        }
        using var output = new PdfBuffer(limit, position);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var source = new MemoryStream(input, false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            int read;
            while ((read = inflater.Read(buffer.AsSpan(0, Math.Min(8192, limit - output.Count + (output.Count < limit ? 0 : 1))))) > 0)
            {
                output.Add(buffer.AsSpan(0, read));
            }
        }
        catch (InvalidDataException error)
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position, error);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        var result = output.ToArray();
        // ZLibStream can return an incomplete stream without throwing. Verify the RFC 1950 trailer.
        uint a = 1, b = 0;
        foreach (var value in result) { a = (a + value) % 65521; b = (b + a) % 65521; }
        if ((b << 16 | a) != BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(input.Length - 4)))
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position);
        }
        return result;
    }
    private static byte[] AsciiHex(ReadOnlySpan<byte> input, int limit, long position)
    {
        using var output = new PdfBuffer(limit, position);
        var high = -1;
        foreach (var value in input)
        {
            if (value == '>') { if (high >= 0) { output.Add((byte)(high << 4)); } return output.ToArray(); }
            if (PdfInput.IsWhiteSpace(value)) { continue; }
            var digit = PdfName.Hex(value);
            if (digit < 0) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
            if (high < 0) { high = digit; }
            else { output.Add((byte)((high << 4) | digit)); high = -1; }
        }
        throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position);
    }
    private static byte[] Ascii85(ReadOnlySpan<byte> input, int limit, long position)
    {
        using var output = new PdfBuffer(limit, position);
        ulong value = 0;
        var digits = 0;
        Span<byte> tuple = stackalloc byte[4];
        for (var i = 0; i < input.Length; i++)
        {
            var next = input[i];
            if (PdfInput.IsWhiteSpace(next)) { continue; }
            if (next == '~')
            {
                if (++i >= input.Length) { throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position); }
                if (input[i] != '>' || digits == 1) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
                if (digits > 1)
                {
                    var count = digits - 1;
                    while (digits++ < 5) { value = value * 85 + 84; }
                    if (value > uint.MaxValue) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
                    BinaryPrimitives.WriteUInt32BigEndian(tuple, (uint)value);
                    output.Add(tuple[..count]);
                }
                return output.ToArray();
            }
            if (next == 'z' && digits == 0) { tuple.Clear(); output.Add(tuple); continue; }
            if (next is < (byte)'!' or > (byte)'u') { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
            value = value * 85 + (uint)(next - '!');
            if (++digits == 5)
            {
                if (value > uint.MaxValue) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
                BinaryPrimitives.WriteUInt32BigEndian(tuple, (uint)value);
                output.Add(tuple); value = 0; digits = 0;
            }
        }
        throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position);
    }
    private static byte[] RunLength(ReadOnlySpan<byte> input, int limit, long position)
    {
        using var output = new PdfBuffer(limit, position);
        for (var i = 0; i < input.Length;)
        {
            var count = input[i++];
            if (count == 128) { return output.ToArray(); }
            if (count < 128)
            {
                var length = count + 1;
                if (length > input.Length - i) { throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position); }
                output.Add(input.Slice(i, length)); i += length;
            }
            else
            {
                if (i == input.Length) { throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position); }
                var value = input[i++];
                for (var j = 0; j < 257 - count; j++) { output.Add(value); }
            }
        }
        throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position);
    }
    private static byte[] Lzw(ReadOnlySpan<byte> input, int earlyChange, int limit, long position)
    {
        if (earlyChange is not (0 or 1)) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
        var prefixes = ArrayPool<int>.Shared.Rent(4096);
        var suffixes = ArrayPool<byte>.Shared.Rent(4096);
        var stack = ArrayPool<byte>.Shared.Rent(4097);
        using var output = new PdfBuffer(limit, position);
        try
        {
            long bit = 0;
            var width = 9;
            var next = 258;
            var previous = -1;
            while (bit + width <= (long)input.Length * 8)
            {
                var code = 0;
                for (var i = 0; i < width; i++, bit++) { code = (code << 1) | ((input[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1); }
                if (code == 256) { width = 9; next = 258; previous = -1; continue; }
                if (code == 257) { return output.ToArray(); }
                if (code > next || (code == next && previous < 0)) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
                var current = code == next ? previous : code;
                var count = 0;
                while (current >= 256)
                {
                    if (current >= next || count >= 4096) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
                    stack[count++] = suffixes[current]; current = prefixes[current];
                }
                var first = (byte)current;
                stack[count++] = first;
                for (var i = count - 1; i >= 0; i--) { output.Add(stack[i]); }
                if (code == next) { output.Add(first); }
                if (previous >= 0 && next < 4096)
                {
                    prefixes[next] = previous; suffixes[next++] = first;
                    if (width < 12 && next + earlyChange == 1 << width) { width++; }
                }
                previous = code;
            }
            throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(prefixes); ArrayPool<byte>.Shared.Return(suffixes); ArrayPool<byte>.Shared.Return(stack);
        }
    }
    private static int Parameter(PdfDictionary? dictionary, string key, int fallback, Func<IPdfObject, IPdfObject>? resolve, long position)
    {
        var value = dictionary?.Get(key);
        if (value is null or PdfNull) { return fallback; }
        value = resolve?.Invoke(value) ?? value;
        if (value is not PdfInteger number || number.Value < 0 || number.Value > int.MaxValue)
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position);
        }
        return (int)number.Value;
    }
    private static byte[] Predict(byte[] input, PdfDictionary? parameters, int limit, Func<IPdfObject, IPdfObject>? resolve, long position)
    {
        var predictor = Parameter(parameters, "Predictor", 1, resolve, position);
        if (predictor == 1) { return input; }
        var colors = Parameter(parameters, "Colors", 1, resolve, position);
        var bits = Parameter(parameters, "BitsPerComponent", 8, resolve, position);
        var columns = Parameter(parameters, "Columns", 1, resolve, position);
        if (colors == 0 || columns == 0 || bits is not (1 or 2 or 4 or 8 or 16) || (predictor != 2 && predictor is not (>= 10 and <= 15)))
        {
            throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position);
        }
        var samples = (long)colors * columns;
        if (samples > (long)limit * 8 / bits)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position);
        }
        var rowBytes = (int)((samples * bits + 7) / 8);
        if (rowBytes > limit) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position); }
        var stride = (long)rowBytes + (predictor == 2 ? 0 : 1);
        if (input.Length % stride != 0) { throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, position); }
        if (predictor == 2)
        {
            // TIFF horizontal differencing is per component, including packed 1/2/4-bit and 16-bit samples.
            for (var row = 0; row < input.Length; row += rowBytes)
            {
                var bytes = input.AsSpan(row, rowBytes);
                for (long sample = colors; sample < samples; sample++)
                {
                    var value = (ReadSample(bytes, sample * bits, bits) + ReadSample(bytes, (sample - colors) * bits, bits)) & ((1 << bits) - 1);
                    WriteSample(bytes, sample * bits, bits, value);
                }
            }
            return input;
        }
        var rows = (int)(input.Length / stride);
        var output = new byte[rows * rowBytes]; // <= input.Length <= decoded cap
        var pixelBytes = (int)(((long)colors * bits + 7) / 8);
        for (var row = 0; row < rows; row++)
        {
            var source = row * (rowBytes + 1);
            var filter = input[source++];
            if (filter > 4) { throw new ContentFormatException(ErrorMessages.FilterInvalid, ContentFormatErrorCode.InvalidValue, position); }
            var offset = row * rowBytes;
            for (var column = 0; column < rowBytes; column++)
            {
                var left = column < pixelBytes ? 0 : output[offset + column - pixelBytes];
                var up = row == 0 ? 0 : output[offset + column - rowBytes];
                var upperLeft = row == 0 || column < pixelBytes ? 0 : output[offset + column - rowBytes - pixelBytes];
                var prediction = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, upperLeft) };
                output[offset + column] = (byte)(input[source + column] + prediction);
            }
        }
        return output;
    }
    private static int ReadSample(ReadOnlySpan<byte> bytes, long bit, int width)
    {
        var value = 0;
        for (var i = 0; i < width; i++, bit++) { value = (value << 1) | ((bytes[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1); }
        return value;
    }
    private static void WriteSample(Span<byte> bytes, long bit, int width, int value)
    {
        for (var i = width - 1; i >= 0; i--, bit++)
        {
            var index = (int)(bit >> 3); var shift = 7 - (int)(bit & 7);
            bytes[index] = (byte)((bytes[index] & ~(1 << shift)) | (((value >> i) & 1) << shift));
        }
    }
    private static int Paeth(int left, int up, int upperLeft)
    {
        var p = left + up - upperLeft;
        var a = Math.Abs(p - left); var b = Math.Abs(p - up); var c = Math.Abs(p - upperLeft);
        return a <= b && a <= c ? left : b <= c ? up : upperLeft;
    }
}
