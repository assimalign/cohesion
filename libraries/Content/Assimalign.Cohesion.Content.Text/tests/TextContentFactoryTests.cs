using System;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Text.Tests;

public class TextContentFactoryTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: FromString round-trips through OpenText")]
    public void FromString_OpenText_RoundTrips()
    {
        using var content = TextContentFactory.FromString("key: value", name: "doc.yaml");

        content.Encoding.WebName.ShouldBe("utf-8");
        content.Name.ShouldBe("doc.yaml");
        content.CanReopen.ShouldBeTrue();

        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("key: value");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: explicit encoding decodes without detection")]
    public void FromContent_ExplicitEncoding_Decodes()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        using var inner = ContentFactory.FromBytes(encoding.GetBytes("héllo"));
        using var content = TextContentFactory.FromContent(inner, encoding, leaveOpen: true);

        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("héllo");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: detection reads the mark and skips it")]
    public void FromContent_Detection_SkipsByteOrderMark()
    {
        var bytes = Concat([0xFF, 0xFE], new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("hi"));
        using var inner = ContentFactory.FromBytes(bytes);
        using var content = TextContentFactory.FromContent(inner);

        content.Encoding.WebName.ShouldBe("utf-16");
        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("hi");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: detection without a mark uses null-byte patterns")]
    public void FromContent_DetectionWithoutMark_UsesPatterns()
    {
        using var inner = ContentFactory.FromBytes(new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes("a:"));
        using var content = TextContentFactory.FromContent(inner);

        content.Encoding.WebName.ShouldBe("utf-16BE", StringCompareShould.IgnoreCase);
        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("a:");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: detection rejects single-use content")]
    public void FromContent_Detection_RejectsSingleUseContent()
    {
        using var inner = ContentFactory.FromStream(new NonSeekableStream("a"u8.ToArray()));

        Should.Throw<ContentException>(() => TextContentFactory.FromContent(inner));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: owned inner content is disposed with the text content")]
    public void Dispose_OwnedInnerContent_Disposes()
    {
        var inner = ContentFactory.FromBytes("a"u8.ToArray());
        var content = TextContentFactory.FromContent(inner, Encoding.UTF8);

        content.Dispose();

        Should.Throw<ObjectDisposedException>(inner.OpenRead);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: borrowed inner content stays usable")]
    public void Dispose_BorrowedInnerContent_StaysUsable()
    {
        using var inner = ContentFactory.FromBytes("a"u8.ToArray());
        var content = TextContentFactory.FromContent(inner, Encoding.UTF8, leaveOpen: true);

        content.Dispose();

        using var read = inner.OpenRead();
        read.ReadByte().ShouldBe((int)'a');
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
