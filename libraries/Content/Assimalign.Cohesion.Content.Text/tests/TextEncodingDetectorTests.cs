using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Text.Tests;

public class TextEncodingDetectorTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Text] - Detect: byte order marks decide the encoding")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, "utf-8", 3)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, (byte)'a' }, "utf-16BE", 2)]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0x00 }, "utf-16", 2)]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "utf-32BE", 4)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "utf-32", 4)]
    public void Detect_ByteOrderMark_DecidesEncoding(byte[] prefix, string webName, int preambleLength)
    {
        var detection = TextEncodingDetector.Detect(prefix);

        detection.Encoding.WebName.ShouldBe(webName, StringCompareShould.IgnoreCase);
        detection.PreambleLength.ShouldBe(preambleLength);
        detection.DetectedFromByteOrderMark.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - Detect: null-byte patterns decide the encoding without a mark")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, (byte)'a' }, "utf-32BE")]
    [InlineData(new byte[] { (byte)'a', 0x00, 0x00, 0x00 }, "utf-32")]
    [InlineData(new byte[] { 0x00, (byte)'a', 0x00, (byte)'b' }, "utf-16BE")]
    [InlineData(new byte[] { (byte)'a', 0x00, (byte)'b', 0x00 }, "utf-16")]
    public void Detect_NullBytePattern_DecidesEncoding(byte[] prefix, string webName)
    {
        var detection = TextEncodingDetector.Detect(prefix);

        detection.Encoding.WebName.ShouldBe(webName, StringCompareShould.IgnoreCase);
        detection.PreambleLength.ShouldBe(0);
        detection.DetectedFromByteOrderMark.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - Detect: plain and short input defaults to UTF-8")]
    [InlineData(new byte[] { (byte)'k', (byte)'e', (byte)'y', (byte)':' })]
    [InlineData(new byte[] { (byte)'a' })]
    [InlineData(new byte[0])]
    public void Detect_NoMarkOrPattern_DefaultsToUtf8(byte[] prefix)
    {
        var detection = TextEncodingDetector.Detect(prefix);

        detection.Encoding.WebName.ShouldBe("utf-8");
        detection.PreambleLength.ShouldBe(0);
        detection.DetectedFromByteOrderMark.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Detect: detected encodings never emit byte order marks")]
    public void Detect_Encodings_DoNotEmitByteOrderMarks()
    {
        var detection = TextEncodingDetector.Detect(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' });

        detection.Encoding.GetPreamble().ShouldBeEmpty();
    }
}
