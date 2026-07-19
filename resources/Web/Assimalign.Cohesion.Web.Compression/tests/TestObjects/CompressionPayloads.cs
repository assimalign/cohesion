using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Assimalign.Cohesion.Web.Compression.Tests.TestObjects;

/// <summary>
/// Shared payloads and BCL compress/decompress helpers for the compression tests. Round-trip tests
/// build a coded body with these encoders and assert the middleware's decode reproduces the original
/// (and vice versa for the response side), so the fidelity check rides the same BCL codecs the
/// middleware uses.
/// </summary>
internal static class CompressionPayloads
{
    /// <summary>
    /// A compressible JSON body comfortably above the default 1&#8239;KiB threshold (repetitive, so
    /// it compresses well).
    /// </summary>
    public static string LargeJson { get; } = BuildLargeJson();

    /// <summary>A body below the default 1&#8239;KiB threshold.</summary>
    public const string SmallJson = "{\"message\":\"ok\"}";

    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public static string Utf8(byte[] value) => Encoding.UTF8.GetString(value);

    public static byte[] GzipCompress(byte[] data) => Compress(data, static s => new GZipStream(s, CompressionMode.Compress));

    public static byte[] BrotliCompress(byte[] data) => Compress(data, static s => new BrotliStream(s, CompressionMode.Compress));

    public static byte[] DeflateCompress(byte[] data) => Compress(data, static s => new ZLibStream(s, CompressionMode.Compress));

    public static byte[] GzipDecompress(byte[] data) => Decompress(data, static s => new GZipStream(s, CompressionMode.Decompress));

    public static byte[] BrotliDecompress(byte[] data) => Decompress(data, static s => new BrotliStream(s, CompressionMode.Decompress));

    private static byte[] Compress(byte[] data, Func<Stream, Stream> encoderFactory)
    {
        using MemoryStream output = new();
        using (Stream encoder = encoderFactory(output))
        {
            encoder.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data, Func<Stream, Stream> decoderFactory)
    {
        using MemoryStream input = new(data);
        using Stream decoder = decoderFactory(input);
        using MemoryStream output = new();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static string BuildLargeJson()
    {
        StringBuilder builder = new();
        builder.Append("{\"items\":[");
        for (int i = 0; i < 200; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append("{\"id\":").Append(i).Append(",\"name\":\"item-").Append(i).Append("\",\"active\":true}");
        }
        builder.Append("]}");
        return builder.ToString();
    }
}
