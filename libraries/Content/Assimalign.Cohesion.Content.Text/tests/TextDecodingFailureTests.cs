using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary.Tests;

namespace Assimalign.Cohesion.Content.Text.Tests;

public class TextDecodingFailureTests
{
    public static IEnumerable<object[]> InvalidSequences()
    {
        foreach (var (name, hex, _) in UnicodeCorpus.InvalidUtf8())
        {
            foreach (var chunk in new[] { 1, 2, 3, 4, 1024 })
            {
                yield return [name, hex, chunk];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - RFC 3629 §3/§4: strict UTF-8 reports InvalidValue and absolute byte position")]
    [MemberData(nameof(InvalidSequences))]
    public async Task OpenText_InvalidUtf8_ReportsContentFailureAsync(string name, string hex, int chunk)
    {
        // Arrange. Hand-built malformed bytes from the authored corpus, after multiple decoder buffers.
        using var stream = new ChunkedReadStream(new MemoryStream([.. Encoding.UTF8.GetBytes(new string('a', 2047)), .. Convert.FromHexString(hex)]), chunk);
        using var inner = StreamContent.Create(stream);
        using var content = TextContent.Create(inner, new UTF8Encoding(false, true));

        // Act
        using var reader = content.OpenText();
        var error = Should.Throw<ContentFormatException>(() => reader.ReadToEnd());
        using var asyncReader = await content.OpenTextAsync(CancellationToken.None);
        var asyncError = await Should.ThrowAsync<ContentFormatException>(() => asyncReader.ReadToEndAsync(CancellationToken.None));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue, name);
        error.Position.ShouldBe(2047, name);
        error.InnerException.ShouldBeOfType<DecoderFallbackException>();
        asyncError.ErrorCode.ShouldBe(error.ErrorCode);
        asyncError.Position.ShouldBe(error.Position);
    }

    public static IEnumerable<object[]> ReaderPaths()
    {
        foreach (var codePage in new[] { 65001, 1200, 1201, 12000, 12001 })
        {
            foreach (var bom in new[] { false, true })
            {
                foreach (var mode in new[] { "Peek", "Read", "ReadArray", "ReadSpan", "ReadBlock", "ReadLine", "ReadAsync", "ReadArrayAsync", "ReadBlockAsync", "ReadLineAsync", "ReadToEndAsync" })
                {
                    yield return [codePage, bom, mode];
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - Unicode 17.0 §3.9/§3.10: all reader paths translate strict decoding with optional BOM")]
    [MemberData(nameof(ReaderPaths))]
    public async Task OpenText_ReaderEntryPoint_TranslatesFailureAsync(int codePage, bool bom, string mode)
    {
        // Arrange. Authored lone surrogates/out-of-range scalars, after an encoding-specific BOM.
        Encoding encoding = codePage switch
        {
            65001 => new UTF8Encoding(bom, true),
            1200 => new UnicodeEncoding(false, bom, true),
            1201 => new UnicodeEncoding(true, bom, true),
            12000 => new UTF32Encoding(false, bom, true),
            _ => new UTF32Encoding(true, bom, true),
        };
        byte[] invalid = codePage switch
        {
            65001 => [0xF0, 0x9F, 0x98],
            1200 => [0x00, 0xD8],
            1201 => [0xD8, 0x00],
            12000 => [0x00, 0x00, 0x11, 0x00],
            _ => [0x00, 0x11, 0x00, 0x00],
        };
        var prefix = encoding.GetBytes("abc");
        byte[] bytes = [.. encoding.GetPreamble(), .. prefix, .. invalid];
        using var shortReads = new ChunkedReadStream(new MemoryStream(bytes), 1);
        using var stream = new NonSeekableStream(shortReads);
        using var inner = StreamContent.Create(stream);
        using var content = TextContent.Create(inner, encoding);
        using var reader = await content.OpenTextAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<ContentFormatException>(() => ConsumeAsync(reader, mode, CancellationToken.None));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        error.Position.ShouldBe(encoding.GetPreamble().Length + prefix.Length);
        error.InnerException.ShouldBeOfType<DecoderFallbackException>();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Unicode 17.0 §3.9: caller replacement fallback remains unchanged")]
    public void OpenText_CustomReplacement_PreservesFallback()
    {
        // Arrange. Authored invalid byte with an explicit, caller-selected replacement string.
        var encoding = (Encoding)new UTF8Encoding(false).Clone();
        encoding.DecoderFallback = new DecoderReplacementFallback("<bad>");
        using var inner = MemoryContent.Create(new byte[] { (byte)'a', 0xFF, (byte)'b' });
        using var content = TextContent.Create(inner, encoding);

        // Act
        using var reader = content.OpenText();
        var actual = reader.ReadToEnd();

        // Assert
        actual.ShouldBe("a<bad>b");
    }

    private static async Task ConsumeAsync(TextReader reader, string mode, CancellationToken cancellationToken)
    {
        var buffer = new char[7];
        while (true)
        {
            int count;
            switch (mode)
            {
                case "Peek": count = reader.Peek(); if (count >= 0) { reader.Read(); } break;
                case "Read": count = reader.Read(); break;
                case "ReadArray": count = reader.Read(buffer, 0, buffer.Length); break;
                case "ReadSpan": count = reader.Read(buffer.AsSpan()); break;
                case "ReadBlock": count = reader.ReadBlock(buffer); break;
                case "ReadLine": if (reader.ReadLine() is null) { return; } continue;
                case "ReadLineAsync": if (await reader.ReadLineAsync(cancellationToken) is null) { return; } continue;
                case "ReadAsync": count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken); break;
                case "ReadArrayAsync": count = await reader.ReadAsync(buffer, 0, buffer.Length); break;
                case "ReadBlockAsync": count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken); break;
                default: await reader.ReadToEndAsync(cancellationToken); return;
            }

            if (count <= 0)
            {
                return;
            }
        }
    }
}
