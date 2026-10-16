using System;
using System.Buffers.Binary;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffBoxHeaderTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Headers preserve ordinary, large and uuid encodings")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TryRead_ValidHeader_PreservesFields(bool large, bool uuid)
    {
        // Arrange: built from the ISO BMFF header grammar; no downloaded media.
        var size = (large ? 16 : 8) + (uuid ? 16 : 0);
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, large ? 1u : (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)(uuid ? BmffBoxType.uuid : BmffBoxType.FreeSpace));
        if (large)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), (ulong)size);
        }
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        // Act
        BmffBoxHeader.TryRead(input, 100, 100 + size, true, out var header).ShouldBeTrue();
        header.Write(output);
        // Assert
        header.Offset.ShouldBe(100);
        header.HeaderSize.ShouldBe(size);
        header.TotalSize.ShouldBe(size);
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Zero size is restricted to the top level")]
    public void TryRead_ZeroSize_RejectsNestedBox()
    {
        // Arrange
        using var input = new MemoryStream(new byte[8]);
        // Act
        BmffBoxHeader.TryRead(input, 0, 8, true, out var header).ShouldBeTrue();
        input.Position = 0;
        var error = Should.Throw<ContentFormatException>(() => BmffBoxHeader.TryRead(input, 0, 8, false, out _));
        // Assert
        header.ExtendsToEnd.ShouldBeTrue();
        header.TotalSize.ShouldBe(8);
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Truncated headers identify the first missing byte")]
    public void TryRead_PartialHeader_ReportsAbsolutePosition()
    {
        // Arrange
        using var input = new MemoryStream(new byte[7]);
        // Act
        var error = Should.Throw<ContentFormatException>(() => BmffBoxHeader.TryRead(input, 100, null, true, out _));
        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(107);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Nested windows obey short reads and relative seeking")]
    public void Read_NestedWindow_ReturnsOnlyRemainingBytes()
    {
        // Arrange
        using var source = new MemoryStream(new byte[32]);
        using var parent = new BmffStream(source, 8, 16);
        using var child = new BmffStream(parent, 4, 3);
        var bytes = new byte[16];
        // Act
        var read = child.Read(bytes, 2, 12);
        // Assert
        read.ShouldBe(3);
        child.Offset.ShouldBe(12);
        child.Length.ShouldBe(3);
        child.Read(bytes).ShouldBe(0);
        child.Seek(-1, SeekOrigin.End).ShouldBe(2);
        child.Read(bytes).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Writes stay inside the window")]
    public void Write_WindowBoundary_RejectsOverflow()
    {
        // Arrange
        using var source = new MemoryStream();
        using var window = new BmffStream(source, 0, 2);
        // Act
        window.Write(new byte[2]);
        var error = Should.Throw<ContentFormatException>(() => window.WriteByte(1));
        // Assert
        source.Length.ShouldBe(2);
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
}
