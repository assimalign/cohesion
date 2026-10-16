using System;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Text.Tests;

public class TextContentTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: FromString round-trips through OpenText")]
    public void Create_StringInput_RoundTrips()
    {
        using var content = TextContent.Create("key: value", name: "doc.yaml");

        content.Encoding.WebName.ShouldBe("utf-8");
        content.Name.ShouldBe("doc.yaml");
        content.CanReopen.ShouldBeTrue();

        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("key: value");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: explicit encoding decodes without detection")]
    public void Create_ExplicitEncoding_Decodes()
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        using var inner = MemoryContent.Create(encoding.GetBytes("héllo"));
        using var content = TextContent.Create(inner, encoding, leaveOpen: true);

        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("héllo");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: detection reads the mark and skips it")]
    public void Create_Detection_SkipsByteOrderMark()
    {
        var bytes = Concat([0xFF, 0xFE], new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("hi"));
        using var inner = MemoryContent.Create(bytes);
        using var content = TextContent.Create(inner);

        content.Encoding.WebName.ShouldBe("utf-16");
        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("hi");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: detection without a mark uses null-byte patterns")]
    public void Create_DetectionWithoutMark_UsesPatterns()
    {
        using var inner = MemoryContent.Create(new UnicodeEncoding(bigEndian: true, byteOrderMark: false).GetBytes("a:"));
        using var content = TextContent.Create(inner);

        content.Encoding.WebName.ShouldBe("utf-16BE", StringCompareShould.IgnoreCase);
        using var reader = content.OpenText();
        reader.ReadToEnd().ShouldBe("a:");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: detection rejects single-use content")]
    public void Create_Detection_RejectsSingleUseContent()
    {
        using var inner = StreamContent.Create(new NonSeekableStream("a"u8.ToArray()));

        Should.Throw<ContentException>(() => TextContent.Create(inner));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: owned inner content is disposed with the text content")]
    public void Dispose_OwnedInnerContent_Disposes()
    {
        var inner = MemoryContent.Create("a"u8.ToArray());
        var content = TextContent.Create(inner, Encoding.UTF8);

        content.Dispose();

        Should.Throw<ObjectDisposedException>(inner.OpenRead);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Factory: borrowed inner content stays usable")]
    public void Dispose_BorrowedInnerContent_StaysUsable()
    {
        using var inner = MemoryContent.Create("a"u8.ToArray());
        var content = TextContent.Create(inner, Encoding.UTF8, leaveOpen: true);

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

    private sealed class NonSeekableStream : MemoryStream
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NonSeekableStream"/> class.
        /// </summary>
        /// <param name="data">The bytes the stream exposes for reading.</param>
        public NonSeekableStream(byte[] data) : base(data)
        {
        }

        public override bool CanSeek => false;
    }
}
