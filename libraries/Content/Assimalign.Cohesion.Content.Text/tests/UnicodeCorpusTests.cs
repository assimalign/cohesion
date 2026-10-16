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

public class UnicodeCorpusTests
{
    public static IEnumerable<object[]> Encodings()
    {
        foreach (var codePage in new[] { 65001, 1200, 1201, 12000, 12001 })
        {
            foreach (var bom in new[] { false, true })
            {
                foreach (var chunk in new[] { 1, 2, 3, 4, 1024 })
                {
                    yield return [codePage, bom, chunk];
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - Unicode 17.0 §3.9/§3.10, RFC 3629 §6: BOM and markless detection across short reads")]
    [MemberData(nameof(Encodings))]
    public async Task OpenText_UnicodeEncoding_DetectsAndDecodesExactlyAsync(int codePage, bool bom, int chunk)
    {
        // Arrange. Authored text preserves combining sequences and supplementary scalars.
        var expected = "ASCII é 中 \U0001F642 e\u0301 \u00E9 \U0001D11E \U0010FFFF";
        var encoding = UnicodeCorpus.EncodingFor(codePage);
        byte[] bytes = [.. bom ? UnicodeCorpus.PreambleFor(codePage) : [], .. encoding.GetBytes(expected)];
        using var source = new ChunkedReadStream(new MemoryStream(bytes), chunk);
        using var inner = StreamContent.Create(source, leaveOpen: true);
        using var content = TextContent.Create(inner, leaveOpen: true);

        // Act
        using var reader = content.OpenText();
        var actual = reader.ReadToEnd();
        using var asyncReader = await content.OpenTextAsync(CancellationToken.None);
        var asyncActual = await asyncReader.ReadToEndAsync(CancellationToken.None);

        // Assert
        content.Encoding.CodePage.ShouldBe(codePage);
        content.Encoding.GetPreamble().ShouldBeEmpty();
        actual.ShouldBe(expected);
        asyncActual.ShouldBe(expected);
        source.ReadCalls.ShouldBeGreaterThan(0);
        source.AsyncReadCalls.ShouldBeGreaterThan(0);
        var detection = TextEncodingDetector.Detect(bytes.AsSpan(0, Math.Min(4, bytes.Length)));
        detection.PreambleLength.ShouldBe(bom ? UnicodeCorpus.PreambleFor(codePage).Length : 0);
        detection.DetectedFromByteOrderMark.ShouldBe(bom);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - RFC 3629 §3/§4: one to four byte UTF-8 scalar boundaries without BOM")]
    [InlineData("007F", "\0\u007F")]
    [InlineData("C280DFBF", "\u0080\u07FF")]
    [InlineData("E0A080ED9FBFEE8080EFBFBF", "\u0800\uD7FF\uE000\uFFFF")]
    [InlineData("F0908080F48FBFBF", "\U00010000\U0010FFFF")]
    public void OpenText_Utf8ScalarBoundaries_PreservesScalars(string hex, string expected)
    {
        // Arrange. An ASCII prefix prevents the detector's documented NUL-pattern heuristic.
        using var bytes = MemoryContent.Create((byte[])[.. "Text: "u8, .. Convert.FromHexString(hex)]);
        using var content = TextContent.Create(bytes);

        // Act
        using var reader = content.OpenText();
        var actual = reader.ReadToEnd();

        // Assert
        content.Encoding.CodePage.ShouldBe(65001);
        actual.ShouldBe("Text: " + expected);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - Unicode 17.0 §3.9, RFC 3629 §3: supplementary scalar crosses decoder buffer boundary")]
    [InlineData(1021)]
    [InlineData(1022)]
    [InlineData(1023)]
    [InlineData(1024)]
    public async Task OpenText_SupplementaryScalarAcrossBuffer_PreservesPairAsync(int prefixLength)
    {
        // Arrange. Authored UTF-8 splits a four-byte scalar at every internal 1024-byte boundary.
        var expected = new string('a', prefixLength) + "\U0001F642e\u0301";
        using var stream = new ChunkedReadStream(new MemoryStream(Encoding.UTF8.GetBytes(expected)), 1024);
        using var inner = StreamContent.Create(stream);
        using var content = TextContent.Create(inner, new UTF8Encoding(false, true));

        // Act
        using var reader = await content.OpenTextAsync(CancellationToken.None);
        var actual = await reader.ReadToEndAsync(CancellationToken.None);

        // Assert
        actual.ShouldBe(expected);
        stream.AsyncReadCalls.ShouldBeGreaterThan(1);
    }

    public static IEnumerable<object[]> InvalidSequences()
    {
        foreach (var (name, hex, decoded) in UnicodeCorpus.InvalidUtf8())
        {
            foreach (var chunk in new[] { 1, 2, 3, 4, 1024 })
            {
                yield return [name, hex, decoded, chunk];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - RFC 3629 §3/§4, Unicode 17.0 §3.9: exact maximal-subpart replacement")]
    [MemberData(nameof(InvalidSequences))]
    public async Task OpenText_InvalidUtf8_ReplacesExactlyAsync(string name, string hex, string decoded, int chunk)
    {
        // Arrange. Prefix is valid; invalid sequence ends at EOF to exercise decoder flushing.
        using var stream = new ChunkedReadStream(new MemoryStream([.. "abc"u8, .. Convert.FromHexString(hex)]), chunk);
        using var inner = StreamContent.Create(stream);
        using var content = TextContent.Create(inner);

        // Act
        using var reader = content.OpenText();
        var actual = reader.ReadToEnd();
        using var asyncReader = await content.OpenTextAsync(CancellationToken.None);
        var asyncActual = await asyncReader.ReadToEndAsync(CancellationToken.None);

        // Assert
        actual.ShouldBe("abc" + decoded, name);
        asyncActual.ShouldBe(actual);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - Unicode 17.0 §5.8: CR, LF, CRLF recognized; NEL, LS, PS, VT, FF preserved")]
    [InlineData("\n", TextLineEnding.LineFeed)]
    [InlineData("\r", TextLineEnding.CarriageReturn)]
    [InlineData("\r\n", TextLineEnding.CarriageReturnLineFeed)]
    [InlineData("\u0085", TextLineEnding.None)]
    [InlineData("\u2028", TextLineEnding.None)]
    [InlineData("\u2029", TextLineEnding.None)]
    [InlineData("\v", TextLineEnding.None)]
    [InlineData("\f", TextLineEnding.None)]
    public void TryReadLine_UnicodeLineSeparators_UsesDocumentedRecognitionSet(string separator, TextLineEnding ending)
    {
        // Arrange. Hand-authored separators cover Unicode newline guidance without widening scope.
        var source = "e\u0301" + separator + "\U0001F642" + separator;
        using var stream = new ChunkedReadStream(new MemoryStream(Encoding.UTF8.GetBytes(source)), 1);
        using var inner = StreamContent.Create(stream);
        using var content = TextContent.Create(inner, Encoding.UTF8);
        using var lines = new TextLineReader(content.OpenText());

        // Act
        var values = new List<TextLine>();
        while (lines.TryReadLine(out var line))
        {
            values.Add(line);
        }

        // Assert
        values.Count.ShouldBe(ending == TextLineEnding.None ? 1 : 2);
        values[0].Text.ShouldBe(ending == TextLineEnding.None ? source : "e\u0301");
        values[0].Ending.ShouldBe(ending);
        values[0].Number.ShouldBe(1);
        if (values.Count == 2)
        {
            values[1].Text.ShouldBe("\U0001F642");
            values[1].Ending.ShouldBe(ending);
            values[1].Number.ShouldBe(2);
        }
    }
}
