using System;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Exe.Tests;

public sealed class ExecutableMetadataTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: PE metadata preserves widths and header fields")]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_PeHeaders_ModelsMetadata(bool is64)
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableHeaders.Pe(is64));
        IContentReader<IExecutableFile> reader = new PeExecutableReader();

        // Act
        var file = reader.Read(stream);

        // Assert
        file.Format.ShouldBeSameAs(PeExecutableReader.Format);
        file.Bitness.ShouldBe(is64 ? 64 : 32);
        file.Machine.ShouldBe(is64 ? 0x8664U : 0x14CU);
        file.ByteOrder.ShouldBe(ExecutableByteOrder.LittleEndian);
        file.Kind.ShouldBe(ExecutableKind.Library);
        file.EntryPoint.ShouldBe(0x1234UL);
        file.Elf.ShouldBeNull();
        file.MachO.ShouldBeNull();
        var pe = file.Pe.ShouldNotBeNull();
        pe.DosHeader.Length.ShouldBe(64);
        pe.PeHeaderOffset.ShouldBe(64U);
        pe.TimeDateStamp.ShouldBe(123456U);
        pe.Characteristics.ShouldBe((ushort)0x2002);
        pe.OptionalHeaderMagic.ShouldBe(is64 ? (ushort)0x20B : (ushort)0x10B);
        pe.ImageBase.ShouldBe(is64 ? 0x140000000UL : 0x400000UL);
        pe.Subsystem.ShouldBe((ushort)3);
        pe.DllCharacteristics.ShouldBe((ushort)0x140);
        pe.SectionAlignment.ShouldBe(0x1000U);
        pe.FileAlignment.ShouldBe(0x200U);
        pe.SizeOfCode.ShouldBe(0x200U);
        pe.SizeOfInitializedData.ShouldBe(0x400U);
        pe.SizeOfUninitializedData.ShouldBe(0x600U);
        pe.SizeOfImage.ShouldBe(0x3000U);
        pe.SizeOfHeaders.ShouldBe((uint)stream.Length);
        pe.IsManaged.ShouldBeFalse();
        pe.Sections.ShouldBeEmpty();
        pe.DataDirectories.ShouldBeEmpty();
        stream.CanRead.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: ELF types retain identification and raw machine")]
    [InlineData(false, false, 1, ExecutableKind.Object)]
    [InlineData(true, false, 2, ExecutableKind.Executable)]
    [InlineData(true, true, 3, ExecutableKind.Library)]
    [InlineData(false, true, 4, ExecutableKind.Core)]
    public void Read_ElfHeaders_ModelsMetadata(bool is64, bool big, int type, ExecutableKind kind)
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableHeaders.Elf(is64, big, (ushort)type));

        // Act
        var file = new ExecutableReader().Read(stream);

        // Assert
        file.Format.ShouldBeSameAs(ElfExecutableReader.Format);
        file.Bitness.ShouldBe(is64 ? 64 : 32);
        file.ByteOrder.ShouldBe(big ? ExecutableByteOrder.BigEndian : ExecutableByteOrder.LittleEndian);
        file.Kind.ShouldBe(kind);
        file.Machine.ShouldBe(is64 ? 62U : 3U);
        file.EntryPoint.ShouldBe(is64 ? 0x1234567890UL : 0x12345678UL);
        file.Pe.ShouldBeNull();
        file.MachO.ShouldBeNull();
        var elf = file.Elf.ShouldNotBeNull();
        elf.Class.ShouldBe((byte)(is64 ? 2 : 1));
        elf.DataEncoding.ShouldBe((byte)(big ? 2 : 1));
        elf.OsAbi.ShouldBe((byte)3);
        elf.AbiVersion.ShouldBe((byte)1);
        elf.Type.ShouldBe((ushort)type);
        elf.ProgramHeaders.ShouldBeEmpty();
        elf.Sections.ShouldBeEmpty();
        elf.Interpreter.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Read: Mach-O metadata retains CPU and flags")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_MachOHeaders_ModelsMetadata(bool is64, bool big)
    {
        // Arrange
        using var stream = new MemoryStream(ExecutableHeaders.MachO(is64, big));

        // Act
        var file = new ExecutableReader().Read(stream);

        // Assert
        file.Format.ShouldBeSameAs(MachOExecutableReader.Format);
        file.Bitness.ShouldBe(is64 ? 64 : 32);
        file.ByteOrder.ShouldBe(big ? ExecutableByteOrder.BigEndian : ExecutableByteOrder.LittleEndian);
        file.Kind.ShouldBe(ExecutableKind.Executable);
        file.EntryPoint.ShouldBeNull();
        file.Pe.ShouldBeNull();
        file.Elf.ShouldBeNull();
        var mach = file.MachO.ShouldNotBeNull();
        mach.IsUniversal.ShouldBeFalse();
        mach.CpuType.ShouldBe(is64 ? 0x1000007U : 7U);
        mach.CpuSubtype.ShouldBe(3U);
        mach.FileType.ShouldBe(2U);
        mach.Flags.ShouldBe(0x200085U);
        mach.LoadCommands.ShouldBeEmpty();
        mach.Architectures.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - Create: reader limits reject invalid options")]
    public void Create_InvalidLimits_ThrowsBeforeReading()
    {
        // Arrange
        var options = new ExecutableReaderOptions { MaxTableEntries = 0 };

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => ExecutableReader.Create(options));
        Should.Throw<ArgumentOutOfRangeException>(() => PeExecutableReader.Create(options));
        Should.Throw<ArgumentOutOfRangeException>(() => ElfExecutableReader.Create(options));
        Should.Throw<ArgumentOutOfRangeException>(() => MachOExecutableReader.Create(options));
        Should.Throw<ArgumentOutOfRangeException>(() => ExecutableReader.Create(new() { MaxBufferedBytes = -1 }));
    }
}
