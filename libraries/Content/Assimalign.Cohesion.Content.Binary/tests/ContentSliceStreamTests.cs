using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Binary.Tests;

public class ContentSliceStreamTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Slices: three nested windows never cross a boundary")]
    public void Read_ThreeNestedSlices_RespectsEveryBoundary()
    {
        // Arrange
        using var memory = new MemoryStream(PrimitiveBytes.Sequence(20));
        using var source = new ReadLimitStream(memory, 11);
        using var parent = ContentSliceStream.Create(source, 2, 16);
        using var child = parent.CreateSlice(3, 10);
        using var leaf = child.CreateSlice(2, 4);
        Span<byte> buffer = stackalloc byte[16];

        // Act
        var read = leaf.Read(buffer);
        var end = leaf.Read(buffer);

        // Assert
        read.ShouldBe(4);
        buffer[..read].ToArray().ShouldBe(new byte[] { 7, 8, 9, 10 });
        end.ShouldBe(0);
        leaf.ReadByte().ShouldBe(-1);
        leaf.SourceOffset.ShouldBe(7);
        leaf.Remaining.ShouldBe(0);
        leaf.Position.ShouldBe(4);
        child.Position.ShouldBe(6);
        parent.Position.ShouldBe(9);
        memory.Position.ShouldBe(11);
        source.ReadCalls.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Slices: alternating random windows reposition their shared source")]
    public void Read_AlternatingSlices_PreservesIndependentCursors()
    {
        // Arrange
        using var source = new MemoryStream(PrimitiveBytes.Sequence(16));
        using var first = ContentSliceStream.Create(source, 2, 3);
        using var second = ContentSliceStream.Create(source, 9, 3);

        // Act / Assert
        first.ReadByte().ShouldBe(2);
        second.ReadByte().ShouldBe(9);
        first.ReadByte().ShouldBe(3);
        source.Position = 0;
        second.ReadByte().ShouldBe(10);
        first.Position.ShouldBe(2);
        second.Position.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Slices: every read overload handles chunked delivery and the boundary")]
    [InlineData("array")]
    [InlineData("span")]
    [InlineData("asyncArray")]
    [InlineData("memory")]
    [InlineData("byte")]
    public async Task Read_ChunkedOverloads_ReturnsShortCountsWithoutCrossingEnd(string path)
    {
        // Arrange
        using var source = new ReadLimitStream(new MemoryStream(PrimitiveBytes.Sequence(12)), 7);
        using var chunks = new ChunkedReadStream(source, 2);
        using var slice = ContentSliceStream.Create(chunks, 2, 5);
        byte[] buffer = new byte[10];
        using var destination = new MemoryStream();

        // Act
        while (true)
        {
            int read;
            if (path == "byte")
            {
                var value = slice.ReadByte();
                if (value < 0)
                {
                    break;
                }
                destination.WriteByte((byte)value);
                continue;
            }
            read = path switch
            {
                "array" => slice.Read(buffer, 1, 8),
                "span" => slice.Read(buffer.AsSpan(1, 8)),
                "asyncArray" => await slice.ReadAsync(buffer, 1, 8, CancellationToken.None),
                _ => await slice.ReadAsync(buffer.AsMemory(1, 8), CancellationToken.None)
            };
            if (read == 0)
            {
                break;
            }
            read.ShouldBeInRange(1, 2);
            destination.Write(buffer, 1, read);
        }

        // Assert
        destination.ToArray().ShouldBe(new byte[] { 2, 3, 4, 5, 6 });
        slice.Position.ShouldBe(5);
        slice.Remaining.ShouldBe(0);
        source.Position.ShouldBe(7);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Slices: seeking follows FileStream origins without escaping on reads")]
    public void Seek_InsideAndPastWindow_UsesRelativePositions()
    {
        // Arrange
        using var source = new MemoryStream(PrimitiveBytes.Sequence(12));
        using var slice = ContentSliceStream.Create(source, 3, 5);

        // Act / Assert
        slice.Seek(2, SeekOrigin.Begin).ShouldBe(2);
        slice.ReadByte().ShouldBe(5);
        slice.Seek(-1, SeekOrigin.Current).ShouldBe(2);
        slice.Seek(-1, SeekOrigin.End).ShouldBe(4);
        slice.ReadByte().ShouldBe(7);
        slice.Seek(3, SeekOrigin.End).ShouldBe(8);
        slice.ReadByte().ShouldBe(-1);
        slice.Remaining.ShouldBe(0);
        source.Position.ShouldBe(8);
        slice.Position = 1;
        slice.ReadByte().ShouldBe(4);
        Should.Throw<IOException>(() => slice.Seek(-3, SeekOrigin.Current));
        slice.Position.ShouldBe(2);
        Should.Throw<ArgumentOutOfRangeException>(() => slice.Position = -1);
        Should.Throw<ArgumentException>(() => slice.Seek(0, (SeekOrigin)99));
        slice.Position = long.MaxValue;
        slice.ReadByte().ShouldBe(-1);
        Should.Throw<IOException>(() => slice.Seek(1, SeekOrigin.Current));
        Should.Throw<IOException>(() => slice.Seek(long.MinValue, SeekOrigin.Current));
        slice.Position.ShouldBe(long.MaxValue);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Forward slices: skipping abandoned children leaves the next sibling")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipToEnd_ThreeForwardSlices_LeavesParentAtNextByte(bool asynchronous)
    {
        // Arrange
        using var memory = new MemoryStream(PrimitiveBytes.Sequence(16));
        using var chunks = new ChunkedReadStream(memory, 1);
        using var source = new NonSeekableStream(chunks);
        using var parent = ContentSliceStream.CreateForwardOnly(source, 12);
        using var child = parent.CreateSlice(2, 7);
        using var leaf = child.CreateSlice(1, 3);

        // Act
        leaf.ReadByte().ShouldBe(3);
        if (asynchronous)
        {
            await leaf.SkipToEndAsync(CancellationToken.None);
            await child.SkipToEndAsync(CancellationToken.None);
        }
        else
        {
            leaf.SkipToEnd();
            child.SkipToEnd();
        }

        // Assert
        parent.CanSeek.ShouldBeFalse();
        leaf.SourceOffset.ShouldBe(3);
        leaf.Position.ShouldBe(3);
        child.Position.ShouldBe(7);
        parent.Position.ShouldBe(9);
        parent.ReadByte().ShouldBe(9);
        if (asynchronous)
        {
            await parent.SkipToEndAsync(CancellationToken.None);
        }
        else
        {
            parent.SkipToEnd();
        }
        source.ReadByte().ShouldBe(12);
        Should.Throw<NotSupportedException>(() => parent.Position = 0);
        Should.Throw<NotSupportedException>(() => parent.Seek(0, SeekOrigin.Begin));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Forward slices: source origins are captured when available")]
    public void CreateForwardOnly_PositionedSources_CapturesSourceOffset()
    {
        // Arrange
        using var memory = new MemoryStream(PrimitiveBytes.Sequence(20));
        memory.Position = 7;
        using var forward = ContentSliceStream.CreateForwardOnly(memory, 4);

        // Act / Assert
        forward.SourceOffset.ShouldBe(7);
        forward.Position.ShouldBe(0);
        forward.ReadByte().ShouldBe(7);
        forward.CanSeek.ShouldBeFalse();
        using var random = ContentSliceStream.Create(memory, 4, 10);
        random.Position = 3;
        using var nested = ContentSliceStream.CreateForwardOnly(random, 2);
        nested.SourceOffset.ShouldBe(7);
        nested.ReadByte().ShouldBe(7);
        using var unpositioned = new NonSeekableStream(memory, leaveOpen: true);
        using var unknown = ContentSliceStream.CreateForwardOnly(unpositioned, 1);
        unknown.SourceOffset.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Slices: an early source end remains a normal short read")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_SourceShorterThanWindow_ReturnsZeroWithoutThrowing(bool asynchronous)
    {
        // Arrange
        using var source = new NonSeekableStream(new ChunkedReadStream(new MemoryStream(new byte[] { 1, 2, 3 }), 1));
        using var slice = ContentSliceStream.CreateForwardOnly(source, 10);
        var buffer = new byte[20];

        // Act / Assert
        for (var i = 0; i < 3; i++)
        {
            var read = asynchronous ? await slice.ReadAsync(buffer, CancellationToken.None) : slice.Read(buffer);
            read.ShouldBe(1);
        }
        (asynchronous ? await slice.ReadAsync(buffer, CancellationToken.None) : slice.Read(buffer)).ShouldBe(0);
        slice.Position.ShouldBe(3);
        slice.Remaining.ShouldBe(7);
        var error = asynchronous
            ? await Should.ThrowAsync<ContentFormatException>(() => slice.SkipToEndAsync(CancellationToken.None).AsTask())
            : Should.Throw<ContentFormatException>(() => slice.SkipToEnd());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Slices: zero-length windows do not access source reads")]
    public async Task Read_EmptyWindow_ReturnsZero()
    {
        // Arrange
        using var source = new ReadLimitStream(new MemoryStream(new byte[] { 1 }), 0);
        using var slice = ContentSliceStream.Create(source, 0, 0);

        // Act / Assert
        slice.Read(new byte[4]).ShouldBe(0);
        (await slice.ReadAsync(new byte[4], CancellationToken.None)).ShouldBe(0);
        slice.ReadByte().ShouldBe(-1);
        slice.SkipToEnd();
        await slice.SkipToEndAsync(CancellationToken.None);
        source.ReadCalls.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Slices: disposal obeys leaveOpen and is idempotent")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Dispose_OwnedAndBorrowedSources_ClosesOnlyOwnedSource(bool leaveOpen, bool asynchronous, bool forwardOnly)
    {
        // Arrange
        using var source = new TrackingMemoryStream(new byte[] { 1, 2, 3 });
        var slice = forwardOnly ? ContentSliceStream.CreateForwardOnly(source, 3, leaveOpen) : ContentSliceStream.Create(source, 0, 3, leaveOpen);
        using var child = slice.CreateSlice(0, 1);
        child.Dispose();
        slice.CanRead.ShouldBeTrue();

        // Act
        if (asynchronous)
        {
            await slice.DisposeAsync();
            await slice.DisposeAsync();
        }
        else
        {
            slice.Dispose();
            slice.Dispose();
        }

        // Assert
        source.Disposals.ShouldBe(leaveOpen ? 0 : 1);
        source.AsyncDisposals.ShouldBe(!leaveOpen && asynchronous ? 1 : 0);
        source.CanRead.ShouldBe(leaveOpen);
        slice.CanRead.ShouldBeFalse();
        slice.CanSeek.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => slice.ReadByte());
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Slices: read-only contract rejects all writing paths")]
    public async Task Write_ReadOnlyWindow_ThrowsNotSupported()
    {
        // Arrange
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        using var slice = ContentSliceStream.Create(source, 0, 3);

        // Act / Assert
        slice.CanWrite.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => slice.Write(new byte[1], 0, 1));
        Should.Throw<NotSupportedException>(() => slice.Write(new byte[1].AsSpan()));
        Should.Throw<NotSupportedException>(() => slice.WriteByte(1));
        Should.Throw<NotSupportedException>(() => slice.SetLength(1));
        await Should.ThrowAsync<NotSupportedException>(() => slice.WriteAsync(new byte[1], 0, 1, CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(() => slice.WriteAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask());
        source.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Slices: hostile ranges fail without overflow or source access")]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(1, long.MaxValue)]
    [InlineData(long.MinValue, 1)]
    [InlineData(0, long.MinValue)]
    [InlineData(4, 0)]
    [InlineData(2, 2)]
    public void Create_ImpossibleRange_RejectsArguments(long offset, long length)
    {
        // Arrange
        using var source = new ReadLimitStream(new MemoryStream(new byte[3]), 0);
        using var parent = ContentSliceStream.Create(source, 0, 3);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => ContentSliceStream.Create(source, offset, length));
        Should.Throw<ArgumentOutOfRangeException>(() => parent.CreateSlice(offset, length));
        source.ReadCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Forward slices: impossible child and absolute end ranges are rejected")]
    public void CreateForwardOnly_InvalidRangeOrRewind_Rejects()
    {
        // Arrange
        using var memory = new MemoryStream(PrimitiveBytes.Sequence(8));
        using var source = new NonSeekableStream(memory, leaveOpen: true);
        using var parent = ContentSliceStream.CreateForwardOnly(source, 8);
        parent.ReadByte();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => parent.CreateSlice(0, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => parent.CreateSlice(7, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => ContentSliceStream.CreateForwardOnly(source, unchecked((long)ulong.MaxValue)));
        Should.Throw<ArgumentOutOfRangeException>(() => ContentSliceStream.CreateForwardOnly(memory, long.MaxValue));
        Should.Throw<ArgumentException>(() => ContentSliceStream.Create(source, 0, 1));
        Should.Throw<ArgumentNullException>(() => ContentSliceStream.Create(null!, 0, 0));
        Should.Throw<ArgumentNullException>(() => ContentSliceStream.CreateForwardOnly(null!, 0));
    }
}
