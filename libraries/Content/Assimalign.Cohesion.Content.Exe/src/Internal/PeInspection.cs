using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Exe.Internal;

internal static class PeInspection
{
    internal static async ValueTask<IExecutableFile> ReadAsync(InspectionContext context, CancellationToken cancellationToken = default)
    {
        context.Reserve(64, 0);
        var bytes = await context.ReadAtAsync(0, 64, cancellationToken).ConfigureAwait(false);
        var reader = new BinaryContentReader(bytes.Span, context.Origin);
        if (reader.ReadUInt16LittleEndian() != 0x5A4D)
        {
            throw new ContentFormatException(ErrorMessages.InvalidSignature, ContentFormatErrorCode.InvalidStructure, context.Origin);
        }
        reader.ReadBytes(58);
        var peOffset = reader.ReadUInt32LittleEndian();
        if (peOffset < 64)
        {
            throw new ContentFormatException(ErrorMessages.InvalidRange, ContentFormatErrorCode.InvalidStructure, context.Origin + 60);
        }
        context.CheckRange(peOffset, 24, 60);
        var metadata = new PeMetadata { PeHeaderOffset = peOffset, DosHeader = bytes.ToArray() };
        context.Reserve(24, peOffset);
        bytes = await context.ReadAtAsync(peOffset, 24, cancellationToken).ConfigureAwait(false);
        reader = new BinaryContentReader(bytes.Span, context.Origin + peOffset);
        if (reader.ReadUInt32LittleEndian() != 0x00004550)
        {
            throw new ContentFormatException(ErrorMessages.InvalidSignature, ContentFormatErrorCode.InvalidStructure, context.Origin + peOffset);
        }
        metadata.Machine = reader.ReadUInt16LittleEndian();
        var sectionCount = reader.ReadUInt16LittleEndian();
        metadata.TimeDateStamp = reader.ReadUInt32LittleEndian();
        reader.ReadBytes(8); // COFF symbol table is deferred.
        var optionalSize = reader.ReadUInt16LittleEndian();
        metadata.Characteristics = reader.ReadUInt16LittleEndian();
        var optionalOffset = (ulong)peOffset + 24;
        context.CheckRange(optionalOffset, optionalSize, (ulong)peOffset + 20);
        if (optionalSize < 2)
        {
            throw new ContentFormatException(ErrorMessages.InvalidOptionalHeader, ContentFormatErrorCode.InvalidStructure, context.Origin + peOffset + 20);
        }
        bytes = await context.ReadAtAsync(optionalOffset, 2, cancellationToken).ConfigureAwait(false);
        reader = new BinaryContentReader(bytes.Span, context.Origin + (long)optionalOffset);
        metadata.OptionalHeaderMagic = reader.ReadUInt16LittleEndian();
        var is64 = metadata.OptionalHeaderMagic == 0x20B;
        if (!is64 && metadata.OptionalHeaderMagic != 0x10B)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVariant, ContentFormatErrorCode.Unsupported, context.Origin + (long)optionalOffset);
        }
        var fixedSize = is64 ? 112 : 96;
        if (optionalSize < fixedSize)
        {
            throw new ContentFormatException(ErrorMessages.InvalidOptionalHeader, ContentFormatErrorCode.InvalidStructure, context.Origin + peOffset + 20);
        }
        context.Reserve((ulong)fixedSize, optionalOffset);
        bytes = await context.ReadAtAsync(optionalOffset, fixedSize, cancellationToken).ConfigureAwait(false);
        reader = new BinaryContentReader(bytes.Span, context.Origin + (long)optionalOffset);
        reader.ReadBytes(4);
        metadata.SizeOfCode = reader.ReadUInt32LittleEndian();
        metadata.SizeOfInitializedData = reader.ReadUInt32LittleEndian();
        metadata.SizeOfUninitializedData = reader.ReadUInt32LittleEndian();
        metadata.AddressOfEntryPoint = reader.ReadUInt32LittleEndian();
        reader.ReadBytes(is64 ? 4 : 8); // BaseOfCode, and PE32 BaseOfData.
        metadata.ImageBase = is64 ? reader.ReadUInt64LittleEndian() : reader.ReadUInt32LittleEndian();
        metadata.SectionAlignment = reader.ReadUInt32LittleEndian();
        metadata.FileAlignment = reader.ReadUInt32LittleEndian();
        reader.ReadBytes(16); // Version fields and Win32VersionValue.
        metadata.SizeOfImage = reader.ReadUInt32LittleEndian();
        metadata.SizeOfHeaders = reader.ReadUInt32LittleEndian();
        context.CheckRange(0, metadata.SizeOfHeaders, optionalOffset + 60);
        reader.ReadBytes(4); // Checksum.
        metadata.Subsystem = reader.ReadUInt16LittleEndian();
        metadata.DllCharacteristics = reader.ReadUInt16LittleEndian();
        metadata.SizeOfStackReserve = is64 ? reader.ReadUInt64LittleEndian() : reader.ReadUInt32LittleEndian();
        metadata.SizeOfStackCommit = is64 ? reader.ReadUInt64LittleEndian() : reader.ReadUInt32LittleEndian();
        metadata.SizeOfHeapReserve = is64 ? reader.ReadUInt64LittleEndian() : reader.ReadUInt32LittleEndian();
        metadata.SizeOfHeapCommit = is64 ? reader.ReadUInt64LittleEndian() : reader.ReadUInt32LittleEndian();
        reader.ReadBytes(4); // LoaderFlags.
        var directoryCount = reader.ReadUInt32LittleEndian();
        if (directoryCount > (uint)(optionalSize - fixedSize) / 8)
        {
            throw new ContentFormatException(ErrorMessages.InvalidTable, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)optionalOffset + fixedSize - 4);
        }
        var directoryOffset = optionalOffset + (uint)fixedSize;
        var directories = new PeDataDirectory[context.CheckTable(directoryOffset, directoryCount, 8, 8, directoryOffset - 4)];
        for (var i = 0; i < directories.Length; i++)
        {
            var offset = directoryOffset + (uint)i * 8;
            bytes = await context.ReadAtAsync(offset, 8, cancellationToken).ConfigureAwait(false);
            reader = new BinaryContentReader(bytes.Span, context.Origin + (long)offset);
            directories[i] = new PeDataDirectory { VirtualAddress = reader.ReadUInt32LittleEndian(), Size = reader.ReadUInt32LittleEndian() };
            if (i == 4 && directories[i].Size != 0)
            {
                context.CheckRange(directories[i].VirtualAddress, directories[i].Size, offset);
            }
        }
        metadata.DataDirectories = Array.AsReadOnly(directories);
        metadata.IsManaged = directories.Length > 14 && directories[14].VirtualAddress != 0 && directories[14].Size != 0;

        var sectionOffset = optionalOffset + optionalSize;
        var sections = new PeSectionHeader[context.CheckTable(sectionOffset, sectionCount, 40, 40, (ulong)peOffset + 6)];
        for (var i = 0; i < sections.Length; i++)
        {
            var offset = sectionOffset + (uint)i * 40;
            bytes = await context.ReadAtAsync(offset, 40, cancellationToken).ConfigureAwait(false);
            reader = new BinaryContentReader(bytes.Span, context.Origin + (long)offset);
            var nameBytes = reader.ReadBytes(8);
            var nameEnd = nameBytes.IndexOf((byte)0);
            var section = new PeSectionHeader
            {
                Name = Encoding.ASCII.GetString(nameEnd < 0 ? nameBytes : nameBytes[..nameEnd]),
                VirtualSize = reader.ReadUInt32LittleEndian(),
                VirtualAddress = reader.ReadUInt32LittleEndian(),
                SizeOfRawData = reader.ReadUInt32LittleEndian(),
                PointerToRawData = reader.ReadUInt32LittleEndian(),
                PointerToRelocations = reader.ReadUInt32LittleEndian(),
                PointerToLineNumbers = reader.ReadUInt32LittleEndian(),
                NumberOfRelocations = reader.ReadUInt16LittleEndian(),
                NumberOfLineNumbers = reader.ReadUInt16LittleEndian(),
                Characteristics = reader.ReadUInt32LittleEndian()
            };
            if (section.SizeOfRawData != 0)
            {
                context.CheckRange(section.PointerToRawData, section.SizeOfRawData, offset + 20);
            }
            sections[i] = section;
        }
        metadata.Sections = Array.AsReadOnly(sections);
        return new ExecutableFile
        {
            Format = PeExecutableReader.Format, Machine = metadata.Machine, Bitness = is64 ? 64 : 32,
            ByteOrder = ExecutableByteOrder.LittleEndian,
            Kind = (metadata.Characteristics & 0x2000) != 0 ? ExecutableKind.Library : ExecutableKind.Executable,
            EntryPoint = metadata.AddressOfEntryPoint, Pe = metadata
        };
    }
}
