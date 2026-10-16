using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;

namespace Assimalign.Cohesion.Content.Exe.Tests;

public sealed class ExecutableCorpusTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Exe] - ReadAsync: PE directories and section headers match the specification corpus")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_PeCorpus_ReportsSectionsAndCliDirectory(bool is64)
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableCorpus.PeImage(is64));
        using var chunks = new ChunkedReadStream(stream, 3, leaveOpen: true);

        // Act
        var file = await new ExecutableReader().ReadAsync(chunks, CancellationToken.None);

        // Assert
        var metadata = file.Pe.ShouldNotBeNull();
        metadata.IsManaged.ShouldBeTrue();
        metadata.DataDirectories.Count.ShouldBe(16);
        metadata.DataDirectories[14].VirtualAddress.ShouldBe(0x2000U);
        metadata.DataDirectories[14].Size.ShouldBe(72U);
        metadata.Sections.Count.ShouldBe(1);
        var section = metadata.Sections[0];
        section.Name.ShouldBe(".text");
        section.VirtualAddress.ShouldBe(0x2000U);
        section.VirtualSize.ShouldBe(128U);
        section.SizeOfRawData.ShouldBe(128U);
        section.PointerToRawData.ShouldBe(metadata.SizeOfHeaders);
        section.Characteristics.ShouldBe(0x60000020U);
        chunks.AsyncReadCalls.ShouldBeGreaterThan(20);
        chunks.ReadCalls.ShouldBe(0);
        stream.CanRead.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - ReadAsync: ELF headers tables names and interpreter match the gABI corpus")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task ReadAsync_ElfCorpus_ResolvesTablesAndInterpreter(bool is64, bool big, bool extended)
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableCorpus.ElfImage(is64, big, extended));

        // Act
        var file = await new ExecutableReader().ReadAsync(stream, CancellationToken.None);

        // Assert
        file.Bitness.ShouldBe(is64 ? 64 : 32);
        file.ByteOrder.ShouldBe(big ? ExecutableByteOrder.BigEndian : ExecutableByteOrder.LittleEndian);
        var metadata = file.Elf.ShouldNotBeNull();
        metadata.ProgramHeaders.Count.ShouldBe(1);
        metadata.ProgramHeaders[0].Type.ShouldBe(3U);
        metadata.ProgramHeaders[0].Flags.ShouldBe(4U);
        metadata.ProgramHeaders[0].Alignment.ShouldBe(1UL);
        metadata.Interpreter.ShouldBe("/lib/test-loader");
        metadata.SectionNameTableIndex.ShouldBe(1U);
        metadata.Sections.Count.ShouldBe(3);
        metadata.Sections[0].Name.ShouldBeEmpty();
        metadata.Sections[1].Name.ShouldBe(".shstrtab");
        metadata.Sections[1].Type.ShouldBe(3U);
        metadata.Sections[2].Name.ShouldBe(".text");
        metadata.Sections[2].Size.ShouldBe(4UL);
        metadata.Sections[2].Offset.ShouldBe((ulong)stream.Length - 4);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: Mach-O preserves known and unknown load command headers")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_ThinMachOCorpus_PreservesLoadCommands(bool is64, bool big)
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableCorpus.MachOImage(is64, big));

        // Act
        var file = new ExecutableReader().Read(stream);

        // Assert
        var metadata = file.MachO.ShouldNotBeNull();
        metadata.SizeOfCommands.ShouldBe(32U);
        metadata.LoadCommands.Count.ShouldBe(2);
        metadata.LoadCommands[0].Command.ShouldBe(0x80000028U);
        metadata.LoadCommands[0].Size.ShouldBe(24U);
        metadata.LoadCommands[0].Offset.ShouldBe(is64 ? 32UL : 28UL);
        metadata.LoadCommands[1].Command.ShouldBe(0x12345678U);
        metadata.LoadCommands[1].Size.ShouldBe(8U);
        file.EntryPoint.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: universal Mach-O exposes the architecture table")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_FatMachOCorpus_ReportsArchitectures(bool is64, bool big)
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableCorpus.FatMachO(is64, big));

        // Act
        var file = new ExecutableReader().Read(stream);

        // Assert
        file.Bitness.ShouldBe(0);
        file.Machine.ShouldBe(0U);
        file.Kind.ShouldBe(ExecutableKind.Unknown);
        var metadata = file.MachO.ShouldNotBeNull();
        metadata.IsUniversal.ShouldBeTrue();
        metadata.LoadCommands.ShouldBeEmpty();
        metadata.Architectures.Count.ShouldBe(2);
        metadata.Architectures[0].CpuType.ShouldBe(0x1000007U);
        metadata.Architectures[1].CpuType.ShouldBe(0x100000CU);
        metadata.Architectures[0].Offset.ShouldBe(is64 ? 80UL : 48UL);
        metadata.Architectures[1].Size.ShouldBe(32UL);
        metadata.Architectures[1].Alignment.ShouldBe(4U);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - ReadAsync: the loaded runtime assembly is a managed PE on every OS")]
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The required corpus inspects the file-backed runtime assembly under VSTest; this harness is not published as a single file.")]
    public async Task ReadAsync_RuntimeAssembly_ReportsManagedPe()
    {
        // Arrange: inspect the runtime's already-present assembly; never download or load a fixture assembly.
        using var stream = File.OpenRead(typeof(object).Assembly.Location);

        // Act
        var file = await new ExecutableReader().ReadAsync(stream, CancellationToken.None);

        // Assert
        file.Format.ShouldBeSameAs(PeExecutableReader.Format);
        var metadata = file.Pe.ShouldNotBeNull();
        metadata.IsManaged.ShouldBeTrue();
        metadata.Sections.ShouldNotBeEmpty();
        metadata.DataDirectories[14].Size.ShouldBeGreaterThan(0U);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: every truncated corpus prefix is a classified truncation")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Read_TruncationCorpus_ReportsTruncated(int sample)
    {
        // Arrange
        var (bytes, reader) = Sample(sample);

        // Act / Assert
        TruncationCorpus.AssertPrefixes(bytes, memory =>
        {
            using var stream = new MemoryStream(memory.ToArray());
            reader.Read(stream);
            return true;
        });
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - ReadAsync: every truncated corpus prefix is a classified truncation")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ReadAsync_TruncationCorpus_ReportsTruncated(int sample)
    {
        // Arrange
        var (bytes, reader) = Sample(sample);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        // Act / Assert
        await TruncationCorpus.AssertPrefixesAsync(bytes, async (memory, cancellationToken) =>
        {
            using var stream = new MemoryStream(memory.ToArray());
            await reader.ReadAsync(stream, cancellationToken);
            return true;
        }, cancellationToken: watchdog.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: unknown executable magics are not detection errors")]
    [InlineData("23212F62696E2F73680A")] // Script.
    [InlineData("0061736D01000000")] // WebAssembly.
    [InlineData("CAFEBABE0000003D00100A0001")] // Java class header and constant-pool prefix.
    [InlineData("000102030405060708090A0B")]
    [InlineData("")]
    public void Read_UnknownMagic_DetectsUnknownAndInspectionIsUnsupported(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);
        using var stream = new MemoryStream(bytes);

        // Act / Assert
        ExecutableReader.Detect(bytes).ShouldBeSameAs(ContentFormat.Unknown);
        var error = Should.Throw<ContentFormatException>(() => new ExecutableReader().Read(stream));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        error.Position.ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: non-seekable input fails clearly without reading")]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void Read_NonSeekable_ReportsUnsupported(int sample)
    {
        // Arrange
        var (bytes, reader) = Sample(sample);
        using var memory = new MemoryStream(bytes);
        using var stream = new NonSeekableStream(memory, leaveOpen: true);

        // Act
        var error = Should.Throw<ContentFormatException>(() => reader.Read(stream));

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        error.Position.ShouldBe(0L);
        memory.Position.ShouldBe(0L);
        memory.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - ReadAsync: cancellation preserves caller stream ownership")]
    public async Task ReadAsync_Canceled_LeavesSourceOpen()
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableCorpus.ElfImage(true));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => new ExecutableReader().ReadAsync(stream, cancellation.Token).AsTask());
        stream.CanRead.ShouldBeTrue();
        stream.Position.ShouldBe(0L);
    }

    private static (byte[], IContentReader<IExecutableFile>) Sample(int sample) => sample switch
    {
        0 => (ExecutableCorpus.PeImage(false), new PeExecutableReader()),
        1 => (ExecutableCorpus.PeImage(true), new PeExecutableReader()),
        2 => (ExecutableCorpus.ElfImage(false), new ElfExecutableReader()),
        3 => (ExecutableCorpus.ElfImage(true), new ElfExecutableReader()),
        4 => (ExecutableCorpus.ElfImage(true, true), new ElfExecutableReader()),
        5 => (ExecutableCorpus.MachOImage(true), new MachOExecutableReader()),
        6 => (ExecutableCorpus.FatMachO(), new MachOExecutableReader()),
        _ => throw new ArgumentOutOfRangeException(nameof(sample))
    };
}
