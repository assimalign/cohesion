using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Exe.Tests;

public sealed class ExecutableMalformedInputTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: hostile offsets counts and sizes have absolute classified errors")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public async Task Read_MalformedCorpus_ReportsCodeAndAbsolutePosition(int scenario)
    {
        // Arrange: a nested slice and nonzero cursor exercise the absolute-coordinate contract.
        var (bytes, code, field) = Malformed(scenario);
        var prefixed = new byte[17 + bytes.Length];
        bytes.CopyTo(prefixed, 17);
        using var memory = new MemoryStream(prefixed);
        using var slice = ContentSliceStream.Create(memory, 9, prefixed.Length - 9);
        slice.Position = 8;
        var reader = new ExecutableReader();

        // Act
        var error = Should.Throw<ContentFormatException>(() => reader.Read(slice));
        slice.Position = 8;
        var asyncError = await Should.ThrowAsync<ContentFormatException>(() => reader.ReadAsync(slice, CancellationToken.None).AsTask());

        // Assert
        error.ErrorCode.ShouldBe(code);
        error.Position.ShouldBe(17L + field);
        asyncError.ErrorCode.ShouldBe(code);
        asyncError.Position.ShouldBe(17L + field);
        memory.CanRead.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: per-table and metadata limits bound allocation")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Read_ReaderLimit_ReportsLimitExceeded(int sample, bool countLimit)
    {
        // Arrange
        using var stream = new MemoryStream(Sample(sample));
        var options = countLimit ? new ExecutableReaderOptions { MaxTableEntries = 1 } : new ExecutableReaderOptions { MaxBufferedBytes = 32 };

        // Act
        var error = Should.Throw<ContentFormatException>(() => ExecutableReader.Create(options).Read(stream));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: a metadata budget exactly at the boundary succeeds")]
    [InlineData(2, 64)]
    [InlineData(3, 48)]
    public void Read_ExactMetadataBudget_Succeeds(int sample, int budget)
    {
        // Arrange
        using var source = new MemoryStream(Sample(sample));
        var reader = ExecutableReader.Create(new ExecutableReaderOptions { MaxBufferedBytes = budget });

        // Act
        var file = reader.Read(source);

        // Assert
        file.MachO.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - Read: forward-only slices retain their known origin")]
    public void Read_ForwardOnlySlice_ReportsAbsoluteUnsupported()
    {
        // Arrange
        using var source = new MemoryStream(new byte[100]);
        source.Position = 23;
        using var slice = ContentSliceStream.CreateForwardOnly(source, 77);

        // Act
        var error = Should.Throw<ContentFormatException>(() => new ExecutableReader().Read(slice));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        error.Position.ShouldBe(23L);
        source.Position.ShouldBe(23L);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - Read: sparse 40 GiB sources only read metadata")]
    public void Read_LargeSource_DoesNotBufferPayload()
    {
        // Arrange
        using var source = new SparseExecutableStream(ExecutableHeaders.Pe(true));

        // Act
        var file = new ExecutableReader().Read(source);

        // Assert
        file.Bitness.ShouldBe(64);
        source.BytesRead.ShouldBeLessThan(1024);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - Read: deterministic mutations never leak primitive parsing exceptions")]
    public void Read_MutatedHeaders_UsesFormatExceptionVocabulary()
    {
        // Arrange
        var random = new Random(474);
        var reader = new ExecutableReader();

        // Act / Assert: structurally valid mutations may succeed; every rejected input must be classified.
        for (var sample = 0; sample < 4; sample++)
        {
            for (var attempt = 0; attempt < 250; attempt++)
            {
                var bytes = Sample(sample);
                for (var mutation = 0; mutation < 3; mutation++)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }
                using var stream = new MemoryStream(bytes);
                try { reader.Read(stream); }
                catch (ContentFormatException error)
                {
                    error.ErrorCode.ShouldNotBe(ContentFormatErrorCode.Unspecified);
                    error.Position.ShouldNotBeNull();
                    error.Position.Value.ShouldBeInRange(0L, bytes.Length);
                }
            }
        }
    }

    private static byte[] Sample(int sample) => sample switch
    {
        0 => ExecutableCorpus.PeImage(true),
        1 => ExecutableCorpus.ElfImage(true),
        2 => ExecutableCorpus.MachOImage(true),
        3 => ExecutableCorpus.FatMachO(),
        _ => throw new ArgumentOutOfRangeException(nameof(sample))
    };

    private static (byte[], ContentFormatErrorCode, long) Malformed(int scenario)
    {
        byte[] bytes;
        switch (scenario)
        {
            case 0:
                bytes = ExecutableCorpus.PeImage(true);
                ExecutableHeaders.Put32(bytes, 60, uint.MaxValue);
                return (bytes, ContentFormatErrorCode.Truncated, 60);
            case 1:
                bytes = ExecutableCorpus.PeImage(true);
                ExecutableHeaders.Put16(bytes, 70, 600);
                return (bytes, ContentFormatErrorCode.Truncated, 70);
            case 2:
                bytes = ExecutableCorpus.ElfImage(true);
                ExecutableHeaders.Put16(bytes, 56, 600);
                return (bytes, ContentFormatErrorCode.Truncated, 56);
            case 3:
                bytes = ExecutableCorpus.ElfImage(true);
                ExecutableHeaders.Put16(bytes, 60, 600);
                return (bytes, ContentFormatErrorCode.Truncated, 60);
            case 4:
                bytes = ExecutableCorpus.ElfImage(true);
                ExecutableHeaders.Put64(bytes, 32, ulong.MaxValue);
                return (bytes, ContentFormatErrorCode.Truncated, 56);
            case 5:
                bytes = ExecutableCorpus.MachOImage(true);
                ExecutableHeaders.Put32(bytes, 36, 0xFFFFFFF8);
                return (bytes, ContentFormatErrorCode.Truncated, 36);
            case 6:
                bytes = ExecutableCorpus.MachOImage(true);
                ExecutableHeaders.Put32(bytes, 20, uint.MaxValue);
                return (bytes, ContentFormatErrorCode.Truncated, 20);
            case 7:
                bytes = ExecutableCorpus.FatMachO();
                ExecutableHeaders.Put32(bytes, 4, uint.MaxValue, true);
                return (bytes, ContentFormatErrorCode.LimitExceeded, 4);
            case 8:
                bytes = ExecutableCorpus.PeImage(true);
                ExecutableHeaders.Put32(bytes, 196, 17);
                return (bytes, ContentFormatErrorCode.InvalidStructure, 196);
            case 9:
                bytes = ExecutableCorpus.PeImage(true);
                ExecutableHeaders.Put16(bytes, 88, 0x107);
                return (bytes, ContentFormatErrorCode.Unsupported, 88);
            case 10:
                bytes = ExecutableCorpus.ElfImage(true);
                bytes[4] = 0;
                return (bytes, ContentFormatErrorCode.InvalidValue, 4);
            case 11:
                bytes = ExecutableCorpus.ElfImage(true);
                ExecutableHeaders.Put32(bytes, 184, uint.MaxValue);
                return (bytes, ContentFormatErrorCode.InvalidValue, 184);
            case 12:
                bytes = ExecutableCorpus.ElfImage(true);
                ExecutableHeaders.Put32(bytes, 188, 8);
                return (bytes, ContentFormatErrorCode.InvalidStructure, 62);
            case 13:
                bytes = ExecutableCorpus.FatMachO();
                ExecutableHeaders.Put32(bytes, 16, 0xFFFFFFF0, true);
                return (bytes, ContentFormatErrorCode.Truncated, 16);
            case 14:
                bytes = ExecutableCorpus.MachOImage(true);
                ExecutableHeaders.Put32(bytes, 36, 0);
                return (bytes, ContentFormatErrorCode.InvalidStructure, 36);
            case 15:
                bytes = ExecutableCorpus.ElfImage(true, extended: true);
                ExecutableHeaders.Put64(bytes, 152, ulong.MaxValue);
                return (bytes, ContentFormatErrorCode.LimitExceeded, 60);
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }
}
