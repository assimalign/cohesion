using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class PageTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Page: a caller buffer shorter than one page is refused before reading its header")]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(Page.HeaderSize - 1)]
    [InlineData(Page.Size - 1)]
    public unsafe void Constructor_UndersizedCallerMemory_ShouldThrowBeforeDereferencing(int length)
    {
        // Arrange: even a single byte is safe because the constructor reads no header.
        var bytes = new byte[1];
        fixed (byte* pointer = bytes)
        {
            nint address = (nint)pointer;

            // Act
            var refusal = Should.Throw<ArgumentOutOfRangeException>(() => new Page((byte*)address, length));

            // Assert
            refusal.ParamName.ShouldBe("bufferLength");
            bytes[0].ShouldBe((byte)0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page: null and default pages fail without dereferencing")]
    public unsafe void AsSpan_UninitializedPage_ShouldThrowBeforeDereferencing()
    {
        // Arrange
        Page page = default;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new Page(null));
        Should.Throw<ArgumentNullException>(() => new Page(null, Page.Size));
        Should.Throw<InvalidOperationException>(() => page.AsSpan());
        Should.Throw<InvalidOperationException>(() => page.AsBodySpan());
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page: an ordinary caller page always exposes exactly one page")]
    public unsafe void AsSpan_NormalPageWithIgnoredOverflowField_ShouldReturnFixedPage()
    {
        // Arrange
        var bytes = new byte[Page.Size + 16];
        fixed (byte* pointer = bytes)
        {
            var page = new Page(pointer, bytes.Length);
            page.OverflowSize = int.MaxValue;

            // Act
            int whole = page.AsSpan().Length;
            int body = page.AsBodySpan().Length;

            // Assert
            whole.ShouldBe(Page.Size);
            body.ShouldBe(Page.Size - Page.HeaderSize);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Page: caller-owned overflow headers cannot expose bytes beyond the page")]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(int.MinValue, false)]
    [InlineData(int.MinValue, true)]
    [InlineData(Page.Size - Page.HeaderSize + 1, false)]
    [InlineData(Page.Size - Page.HeaderSize + 1, true)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public unsafe void AsSpan_MalformedCallerOwnedHeader_ShouldThrowCorruption(int overflowSize, bool bodyOnly)
    {
        // Arrange
        var bytes = new byte[Page.Size];
        fixed (byte* pointer = bytes)
        {
            var page = new Page(pointer);
            page.Id = 71;
            page.Flags = PageFlags.Overflow;
            page.OverflowSize = overflowSize;

            // Act: inspecting the returned length is safe even against the faulty implementation.
            var refusal = Should.Throw<StorageCorruptionException>(() =>
            {
                int length = bodyOnly ? page.AsBodySpan().Length : page.AsSpan().Length;
                GC.KeepAlive(length);
            });

            // Assert
            refusal.PageId.ShouldBe((PageId)71L);
            refusal.Message.ShouldContain("overflow");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Page: valid reserved overflow lengths stay within caller memory")]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(Page.Size - Page.HeaderSize)]
    public unsafe void AsSpan_ValidOverflowHeader_ShouldReturnBoundedSpans(int overflowSize)
    {
        // Arrange
        var bytes = new byte[Page.Size + 16];
        bytes.AsSpan(Page.Size).Fill(0xA5);
        fixed (byte* pointer = bytes)
        {
            var page = new Page(pointer);
            page.Flags = PageFlags.Overflow;
            page.OverflowSize = overflowSize;

            // Act
            int whole = page.AsSpan().Length;
            page.AsBodySpan().Fill(0x11);

            // Assert
            whole.ShouldBe(Page.HeaderSize + overflowSize);
            page.AsBodySpan().Length.ShouldBe(overflowSize);
            bytes.AsSpan(Page.Size).ToArray().ShouldAllBe(value => value == 0xA5);
        }
    }
}
