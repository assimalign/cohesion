using System;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Tests;

public class MemoryContentTests
{
    [Fact(DisplayName = "Cohesion Test [Content] - Memory: FromBytes content is reopenable and reads the same data")]
    public void FromBytes_ReadTwice_ReturnsSameData()
    {
        byte[] data = [1, 2, 3, 4];
        using var content = ContentFactory.FromBytes(data);

        content.CanReopen.ShouldBeTrue();
        content.Length.ShouldBe(4);
        content.IsReadOnly.ShouldBeTrue();

        using (var first = content.OpenRead())
        {
            ReadAll(first).ShouldBe(data);
        }

        using var second = content.OpenRead();
        ReadAll(second).ShouldBe(data);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Memory: disposed content rejects reads")]
    public void OpenRead_AfterDispose_Throws()
    {
        var content = ContentFactory.FromBytes(new byte[] { 1 });
        content.Dispose();

        Should.Throw<ObjectDisposedException>(content.OpenRead);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Memory: read-only content rejects writes")]
    public void OpenWrite_OnReadOnlyContent_Throws()
    {
        using var content = (IWritableContent)ContentFactory.FromBytes(new byte[] { 1 });

        Should.Throw<NotSupportedException>(content.OpenWrite);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Memory: buffer commits written bytes on write-stream dispose")]
    public void CreateBuffer_Write_CommitsOnDispose()
    {
        byte[] data = [9, 8, 7];
        using var content = ContentFactory.CreateBuffer(name: "buffer");

        content.IsReadOnly.ShouldBeFalse();
        content.Length.ShouldBe(0);

        using (var write = content.OpenWrite())
        {
            write.Write(data);
        }

        content.Length.ShouldBe(3);
        using var read = content.OpenRead();
        ReadAll(read).ShouldBe(data);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Memory: metadata flows from the factory")]
    public void FromBytes_Metadata_IsPreserved()
    {
        var format = new ContentFormat { Name = "YAML", Kind = ContentKind.Document, MediaTypes = ["application/yaml"] };
        using var content = ContentFactory.FromBytes(new byte[] { 1 }, format, name: "openapi.yaml", mediaType: "application/yaml");

        content.Name.ShouldBe("openapi.yaml");
        content.Format.ShouldBeSameAs(format);
        content.MediaType.ShouldBe("application/yaml");
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
