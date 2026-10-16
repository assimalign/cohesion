using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Binary.Tests;

public class ContentStreamExtensionsTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Exact operations: chunked sync delivery never over-reads")]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(7, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    public void ReadSkipCopy_ChunkedSource_ProcessesExactCount(int chunkSize, bool nonSeekable)
    {
        // Arrange: a sentinel tail beyond the requested range.
        var data = PrimitiveBytes.Sequence(23);
        foreach (var operation in new[] { "try", "read", "skip", "copy" })
        {
            using var memory = new MemoryStream(data);
            using var limit = new ReadLimitStream(memory, 17);
            var chunks = new ChunkedReadStream(limit, chunkSize);
            using Stream source = nonSeekable ? new NonSeekableStream(chunks) : chunks;
            using var destination = new MemoryStream();
            var buffer = new byte[17];

            // Act
            switch (operation)
            {
                case "try": source.TryReadExactly(buffer).ShouldBeTrue(); break;
                case "read": source.ReadContentExactly(buffer); break;
                case "skip": source.SkipExactly(17); break;
                case "copy": source.CopyExactlyTo(destination, 17); break;
            }

            // Assert
            memory.Position.ShouldBe(17);
            if (operation is "try" or "read")
            {
                buffer.ShouldBe(data[..17]);
            }
            if (operation == "copy")
            {
                destination.ToArray().ShouldBe(data[..17]);
            }
            if (operation == "skip" && !nonSeekable)
            {
                chunks.ReadCalls.ShouldBe(0);
            }
            else
            {
                chunks.ReadCalls.ShouldBeGreaterThan(1);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Exact operations: chunked async delivery never over-reads")]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(7, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    public async Task ReadSkipCopyAsync_ChunkedSource_ProcessesExactCount(int chunkSize, bool nonSeekable)
    {
        // Arrange
        var data = PrimitiveBytes.Sequence(23);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var operation in new[] { "try", "read", "skip", "copy" })
        {
            using var memory = new MemoryStream(data);
            using var limit = new ReadLimitStream(memory, 17);
            var chunks = new ChunkedReadStream(limit, chunkSize);
            await using Stream source = nonSeekable ? new NonSeekableStream(chunks) : chunks;
            using var destination = new MemoryStream();
            var buffer = new byte[17];

            // Act
            switch (operation)
            {
                case "try": (await source.TryReadExactlyAsync(buffer, cancellation.Token)).ShouldBeTrue(); break;
                case "read": await source.ReadContentExactlyAsync(buffer, cancellation.Token); break;
                case "skip": await source.SkipExactlyAsync(17, cancellation.Token); break;
                case "copy": await source.CopyExactlyToAsync(destination, 17, cancellation.Token); break;
            }

            // Assert
            memory.Position.ShouldBe(17);
            if (operation is "try" or "read")
            {
                buffer.ShouldBe(data[..17]);
            }
            if (operation == "copy")
            {
                destination.ToArray().ShouldBe(data[..17]);
            }
            chunks.ReadCalls.ShouldBe(0);
            if (operation == "skip" && !nonSeekable)
            {
                chunks.AsyncReadCalls.ShouldBe(0);
            }
            else
            {
                chunks.AsyncReadCalls.ShouldBeGreaterThan(1);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Exact operations: zero lengths never touch streams")]
    public async Task ReadSkipCopy_ZeroLength_SucceedsWithoutTouchingStreams()
    {
        // Arrange
        using var source = new UntouchableStream();
        using var destination = new UntouchableStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        source.TryReadExactly(Span<byte>.Empty).ShouldBeTrue();
        source.ReadContentExactly(Span<byte>.Empty);
        source.SkipExactly(0);
        source.CopyExactlyTo(destination, 0);
        (await source.TryReadExactlyAsync(Memory<byte>.Empty, cancellation.Token)).ShouldBeTrue();
        await source.ReadContentExactlyAsync(Memory<byte>.Empty, cancellation.Token);
        await source.SkipExactlyAsync(0, cancellation.Token);
        await source.CopyExactlyToAsync(destination, 0, cancellation.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Exact reads: clean end differs from a partial element")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryReadExactly_EndAndPartial_ReportsCorrectCodeAndPosition(bool nonSeekable)
    {
        // Arrange / Act / Assert
        foreach (var length in new[] { 0, 3 })
        {
            foreach (var asynchronous in new[] { false, true })
            {
                using var memory = new MemoryStream(PrimitiveBytes.Sequence(length));
                var chunks = new ChunkedReadStream(memory, 1);
                using Stream source = nonSeekable ? new NonSeekableStream(chunks) : chunks;
                var bytes = new byte[4];
                if (length == 0)
                {
                    var result = asynchronous
                        ? await source.TryReadExactlyAsync(bytes, CancellationToken.None)
                        : source.TryReadExactly(bytes);
                    result.ShouldBeFalse();
                }
                else
                {
                    var error = asynchronous
                        ? await Should.ThrowAsync<ContentFormatException>(() => source.TryReadExactlyAsync(bytes, CancellationToken.None).AsTask())
                        : Should.Throw<ContentFormatException>(() => source.TryReadExactly(bytes));
                    error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
                    error.Position.ShouldBe(nonSeekable ? null : (long?)length);
                }
                var exactError = asynchronous
                    ? await Should.ThrowAsync<ContentFormatException>(() => source.ReadContentExactlyAsync(bytes, CancellationToken.None).AsTask())
                    : Should.Throw<ContentFormatException>(() => source.ReadContentExactly(bytes));
                exactError.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
                exactError.Position.ShouldBe(nonSeekable ? null : (long?)length);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Exact stream errors: slices supply tracked window positions")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadContentExactly_SliceShortfall_ReportsTrackedPosition(bool forwardOnly)
    {
        // Arrange
        using var source = new MemoryStream(PrimitiveBytes.Sequence(12));
        source.Position = 5;
        using var slice = forwardOnly ? ContentSliceStream.CreateForwardOnly(source, 3) : ContentSliceStream.Create(source, 5, 3);

        // Act
        var error = await Should.ThrowAsync<ContentFormatException>(() => slice.ReadContentExactlyAsync(new byte[4], CancellationToken.None).AsTask());

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(3);
        slice.SourceOffset.ShouldBe(5);
        slice.Position.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Exact skips and copies: huge shortfalls are truncation without overflow")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipCopy_TooManyBytes_ReportsTruncated(bool nonSeekable)
    {
        // Arrange / Act / Assert
        foreach (var copy in new[] { false, true })
        {
            foreach (var asynchronous in new[] { false, true })
            {
                using var memory = new MemoryStream(new byte[] { 1, 2, 3 });
                var chunks = new ChunkedReadStream(memory, 1);
                using Stream source = nonSeekable ? new NonSeekableStream(chunks) : chunks;
                using var destination = new MemoryStream();
                ContentFormatException error;
                if (asynchronous)
                {
                    error = await Should.ThrowAsync<ContentFormatException>(() =>
                        (copy ? source.CopyExactlyToAsync(destination, long.MaxValue, CancellationToken.None)
                              : source.SkipExactlyAsync(long.MaxValue, CancellationToken.None)).AsTask());
                }
                else
                {
                    error = Should.Throw<ContentFormatException>(() =>
                    {
                        if (copy)
                        {
                            source.CopyExactlyTo(destination, long.MaxValue);
                        }
                        else
                        {
                            source.SkipExactly(long.MaxValue);
                        }
                    });
                }
                error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
                error.Position.ShouldBe(nonSeekable ? null : 3L);
                memory.Position.ShouldBe(3);
                if (copy)
                {
                    destination.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
                }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Exact stream corpus: every prefix and configurable empty input")]
    public async Task TryReadExactly_TruncationCorpus_ProtectsBothReadPaths()
    {
        // Arrange: hand-built four-byte field.
        byte[] data = [1, 2, 3, 4];

        // Act / Assert
        TruncationCorpus.AssertPrefixes(data, prefix =>
        {
            using var source = new ChunkedReadStream(new MemoryStream(prefix.ToArray()), 1);
            return source.TryReadExactly(new byte[4]);
        }, allowEmpty: true);
        await TruncationCorpus.AssertPrefixesAsync(data, async (prefix, token) =>
        {
            using var source = new ChunkedReadStream(new MemoryStream(prefix.ToArray()), 1);
            return await source.TryReadExactlyAsync(new byte[4], token);
        }, allowEmpty: true, cancellationToken: CancellationToken.None);
        TruncationCorpus.AssertPrefixes(data, prefix =>
        {
            using var source = new MemoryStream(prefix.ToArray());
            source.ReadContentExactly(new byte[4]);
            return true;
        });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Exact copy: pooled chunks preserve a payload larger than a buffer")]
    public async Task CopyExactlyTo_LargeChunkedPayload_CopiesThrough()
    {
        // Arrange
        var data = PrimitiveBytes.Sequence(200003);
        using var source = new NonSeekableStream(new ChunkedReadStream(new MemoryStream(data), 997));
        using var destination = new MemoryStream();

        // Act
        await source.CopyExactlyToAsync(destination, data.Length, CancellationToken.None);

        // Assert
        destination.ToArray().ShouldBe(data);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Exact operations: cancellation prevents nonempty work")]
    public async Task ReadSkipCopyAsync_CanceledToken_DoesNotAdvance()
    {
        // Arrange
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => source.TryReadExactlyAsync(new byte[1], cancellation.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => source.ReadContentExactlyAsync(new byte[1], cancellation.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => source.SkipExactlyAsync(1, cancellation.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() => source.CopyExactlyToAsync(destination, 1, cancellation.Token).AsTask());
        source.Position.ShouldBe(0);
        destination.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Exact operations: negative counts and null destinations are rejected")]
    public async Task SkipCopy_InvalidArguments_RejectsBeforeReading()
    {
        // Arrange
        using var source = new UntouchableStream();
        using var destination = new UntouchableStream();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => source.SkipExactly(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => source.CopyExactlyTo(destination, -1));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => source.SkipExactlyAsync(-1, CancellationToken.None).AsTask());
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => source.CopyExactlyToAsync(destination, -1, CancellationToken.None).AsTask());
        Should.Throw<ArgumentNullException>(() => source.CopyExactlyTo(null!, 1));
        await Should.ThrowAsync<ArgumentNullException>(() => source.CopyExactlyToAsync(null!, 1, CancellationToken.None).AsTask());
    }
}
