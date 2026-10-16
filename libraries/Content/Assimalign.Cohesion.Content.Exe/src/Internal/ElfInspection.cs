using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Exe.Internal;

internal static class ElfInspection
{
    internal static async ValueTask<IExecutableFile> ReadAsync(InspectionContext context, CancellationToken cancellationToken = default)
    {
        var bytes = await context.ReadAtAsync(0, 16, cancellationToken).ConfigureAwait(false);
        var ident = new BinaryContentReader(bytes.Span, context.Origin);
        if (ident.ReadUInt32BigEndian() != 0x7F454C46)
        {
            throw new ContentFormatException(ErrorMessages.InvalidSignature, ContentFormatErrorCode.InvalidStructure, context.Origin);
        }
        var elfClass = ident.ReadByte();
        var encoding = ident.ReadByte();
        if (elfClass is not (1 or 2) || encoding is not (1 or 2))
        {
            throw new ContentFormatException(ErrorMessages.InvalidIdentification, ContentFormatErrorCode.InvalidValue, context.Origin + (elfClass is not (1 or 2) ? 4 : 5));
        }
        if (ident.ReadByte() != 1)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVariant, ContentFormatErrorCode.Unsupported, context.Origin + 6);
        }
        var metadata = new ElfMetadata { Class = elfClass, DataEncoding = encoding, OsAbi = ident.ReadByte(), AbiVersion = ident.ReadByte() };
        var is64 = elfClass == 2;
        var big = encoding == 2;
        var headerSize = is64 ? 64 : 52;
        context.Reserve((ulong)headerSize, 0);
        bytes = await context.ReadAtAsync(0, headerSize, cancellationToken).ConfigureAwait(false);
        var reader = new EndianReader(bytes.Span, context.Origin, big);
        reader.Bytes(16);
        metadata.Type = reader.UInt16();
        metadata.Machine = reader.UInt16();
        if (reader.UInt32() != 1)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVariant, ContentFormatErrorCode.Unsupported, context.Origin + 20);
        }
        metadata.EntryPoint = reader.Word(is64);
        metadata.ProgramHeaderOffset = reader.Word(is64);
        metadata.SectionHeaderOffset = reader.Word(is64);
        metadata.Flags = reader.UInt32();
        var declaredHeaderSize = reader.UInt16();
        var phStride = reader.UInt16();
        uint phCount = reader.UInt16();
        var shStride = reader.UInt16();
        ulong shCount = reader.UInt16();
        uint nameIndex = reader.UInt16();
        var tailOffset = is64 ? 52UL : 40UL;
        if (declaredHeaderSize < headerSize)
        {
            throw new ContentFormatException(ErrorMessages.InvalidHeaderSize, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)tailOffset);
        }
        context.CheckRange(0, declaredHeaderSize, tailOffset);
        var minimumSectionSize = is64 ? 64U : 40U;
        if ((shCount == 0 && metadata.SectionHeaderOffset != 0) || phCount == 0xFFFF || nameIndex == 0xFFFF)
        {
            context.CheckTable(metadata.SectionHeaderOffset, 1, shStride, minimumSectionSize, tailOffset + 6);
            var zero = await ReadSectionAsync(context, metadata.SectionHeaderOffset, is64, big, cancellationToken).ConfigureAwait(false);
            if (zero.Type != 0)
            {
                throw new ContentFormatException(ErrorMessages.InvalidTable, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)metadata.SectionHeaderOffset + 4);
            }
            if (shCount == 0)
            {
                shCount = zero.Size;
            }
            if (phCount == 0xFFFF)
            {
                phCount = zero.Info;
            }
            if (nameIndex == 0xFFFF)
            {
                nameIndex = zero.Link;
            }
        }
        metadata.SectionNameTableIndex = nameIndex;
        var programs = new ElfProgramHeader[context.CheckTable(metadata.ProgramHeaderOffset, phCount, phStride, is64 ? 56U : 32U, tailOffset + 4)];
        for (var i = 0; i < programs.Length; i++)
        {
            var offset = metadata.ProgramHeaderOffset + (ulong)i * phStride;
            bytes = await context.ReadAtAsync(offset, is64 ? 56 : 32, cancellationToken).ConfigureAwait(false);
            reader = new EndianReader(bytes.Span, context.Origin + (long)offset, big);
            var program = new ElfProgramHeader { Type = reader.UInt32() };
            if (is64)
            {
                program.Flags = reader.UInt32();
            }
            program.Offset = reader.Word(is64);
            program.VirtualAddress = reader.Word(is64);
            program.PhysicalAddress = reader.Word(is64);
            program.FileSize = reader.Word(is64);
            program.MemorySize = reader.Word(is64);
            if (!is64)
            {
                program.Flags = reader.UInt32();
            }
            program.Alignment = reader.Word(is64);
            if (program.Type != 0)
            {
                context.CheckRange(program.Offset, program.FileSize, offset + (is64 ? 8UL : 4UL));
            }
            if (program.Type == 1 && program.FileSize > program.MemorySize)
            {
                throw new ContentFormatException(ErrorMessages.InvalidSegment, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)offset);
            }
            if (program.Type == 3)
            {
                if (metadata.Interpreter is not null || program.FileSize == 0)
                {
                    throw new ContentFormatException(ErrorMessages.InvalidInterpreter, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)offset);
                }
                context.Reserve(program.FileSize, offset);
                var buffer = ArrayPool<byte>.Shared.Rent((int)program.FileSize);
                try
                {
                    await context.ReadIntoAsync(program.Offset, buffer.AsMemory(0, (int)program.FileSize), cancellationToken).ConfigureAwait(false);
                    if (buffer[(int)program.FileSize - 1] != 0)
                    {
                        throw new ContentFormatException(ErrorMessages.UnterminatedString, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)program.Offset);
                    }
                    metadata.Interpreter = context.ReadName(buffer.AsSpan(0, (int)program.FileSize), 0, program.Offset, offset);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            programs[i] = program;
        }
        metadata.ProgramHeaders = Array.AsReadOnly(programs);

        var sections = new ElfSectionHeader[context.CheckTable(metadata.SectionHeaderOffset, shCount, shStride, minimumSectionSize, tailOffset + 8)];
        for (var i = 0; i < sections.Length; i++)
        {
            var offset = metadata.SectionHeaderOffset + (ulong)i * shStride;
            var section = await ReadSectionAsync(context, offset, is64, big, cancellationToken).ConfigureAwait(false);
            // SHT_NULL carries extended counts; SHT_NOBITS has no bytes to read.
            if (section.Type is not (0 or 8))
            {
                context.CheckRange(section.Offset, section.Size, offset + (is64 ? 24UL : 16UL));
            }
            sections[i] = section;
        }
        if (nameIndex != 0)
        {
            if (nameIndex >= (uint)sections.Length || sections[nameIndex].Type != 3 || sections[nameIndex].Size == 0)
            {
                throw new ContentFormatException(ErrorMessages.InvalidNameTable, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)tailOffset + 10);
            }
            var names = sections[nameIndex];
            context.Reserve(names.Size, metadata.SectionHeaderOffset + nameIndex * (ulong)shStride);
            var buffer = ArrayPool<byte>.Shared.Rent((int)names.Size);
            try
            {
                await context.ReadIntoAsync(names.Offset, buffer.AsMemory(0, (int)names.Size), cancellationToken).ConfigureAwait(false);
                if (buffer[0] != 0 || buffer[(int)names.Size - 1] != 0)
                {
                    throw new ContentFormatException(ErrorMessages.InvalidNameTable, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)names.Offset);
                }
                for (var i = 0; i < sections.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sections[i].Name = context.ReadName(buffer.AsSpan(0, (int)names.Size), sections[i].NameOffset,
                        names.Offset, metadata.SectionHeaderOffset + (ulong)i * shStride);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        metadata.Sections = Array.AsReadOnly(sections);
        return new ExecutableFile
        {
            Format = ElfExecutableReader.Format, Machine = metadata.Machine, Bitness = is64 ? 64 : 32,
            ByteOrder = big ? ExecutableByteOrder.BigEndian : ExecutableByteOrder.LittleEndian,
            Kind = metadata.Type switch { 1 => ExecutableKind.Object, 2 => ExecutableKind.Executable, 3 => ExecutableKind.Library, 4 => ExecutableKind.Core, _ => ExecutableKind.Unknown },
            EntryPoint = metadata.EntryPoint, Elf = metadata
        };
    }

    private static async ValueTask<ElfSectionHeader> ReadSectionAsync(
        InspectionContext context, ulong offset, bool is64, bool big, CancellationToken cancellationToken = default)
    {
        var bytes = await context.ReadAtAsync(offset, is64 ? 64 : 40, cancellationToken).ConfigureAwait(false);
        var reader = new EndianReader(bytes.Span, context.Origin + (long)offset, big);
        return new ElfSectionHeader
        {
            NameOffset = reader.UInt32(), Type = reader.UInt32(), Flags = reader.Word(is64),
            Address = reader.Word(is64), Offset = reader.Word(is64), Size = reader.Word(is64),
            Link = reader.UInt32(), Info = reader.UInt32(), AddressAlignment = reader.Word(is64), EntrySize = reader.Word(is64)
        };
    }
}
