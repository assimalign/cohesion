using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Exe.Internal;

internal static class MachOInspection
{
    internal static async ValueTask<IExecutableFile> ReadAsync(InspectionContext context, CancellationToken cancellationToken = default)
    {
        var bytes = await context.ReadAtAsync(0, 4, cancellationToken).ConfigureAwait(false);
        var magicReader = new BinaryContentReader(bytes.Span, context.Origin);
        var magic = magicReader.ReadUInt32BigEndian();
        if (magic is not (0xFEEDFACE or 0xCEFAEDFE or 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA))
        {
            throw new ContentFormatException(ErrorMessages.InvalidSignature, ContentFormatErrorCode.InvalidStructure, context.Origin);
        }
        var big = magic is 0xFEEDFACE or 0xFEEDFACF or 0xCAFEBABE or 0xCAFEBABF;
        var fat = magic is 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA;
        var is64 = magic is 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABF or 0xBFBAFECA;
        var metadata = new MachOMetadata { IsUniversal = fat };
        if (fat)
        {
            context.Reserve(8, 0);
            bytes = await context.ReadAtAsync(0, 8, cancellationToken).ConfigureAwait(false);
            var reader = new EndianReader(bytes.Span, context.Origin, big);
            reader.UInt32();
            var count = reader.UInt32();
            if (count == 0)
            {
                throw new ContentFormatException(ErrorMessages.InvalidTable, ContentFormatErrorCode.InvalidValue, context.Origin + 4);
            }
            var stride = is64 ? 32U : 20U;
            var architectures = new MachOArchitecture[context.CheckTable(8, count, stride, stride, 4)];
            var tableEnd = 8UL + (ulong)architectures.Length * stride;
            for (var i = 0; i < architectures.Length; i++)
            {
                var offset = 8UL + (ulong)i * stride;
                bytes = await context.ReadAtAsync(offset, (int)stride, cancellationToken).ConfigureAwait(false);
                reader = new EndianReader(bytes.Span, context.Origin + (long)offset, big);
                var architecture = new MachOArchitecture
                {
                    CpuType = reader.UInt32(), CpuSubtype = reader.UInt32(),
                    Offset = reader.Word(is64), Size = reader.Word(is64), Alignment = reader.UInt32()
                };
                if (architecture.Offset < tableEnd || architecture.Alignment > (is64 ? 63U : 31U) ||
                    (architecture.Offset & ((1UL << (int)architecture.Alignment) - 1)) != 0)
                {
                    throw new ContentFormatException(ErrorMessages.InvalidArchitecture, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)offset + 8);
                }
                context.CheckRange(architecture.Offset, architecture.Size, offset + 8);
                architectures[i] = architecture;
            }
            metadata.Architectures = Array.AsReadOnly(architectures);
        }
        else
        {
            var headerSize = is64 ? 32 : 28;
            context.Reserve((ulong)headerSize, 0);
            bytes = await context.ReadAtAsync(0, headerSize, cancellationToken).ConfigureAwait(false);
            var reader = new EndianReader(bytes.Span, context.Origin, big);
            reader.UInt32();
            metadata.CpuType = reader.UInt32();
            metadata.CpuSubtype = reader.UInt32();
            metadata.FileType = reader.UInt32();
            var count = reader.UInt32();
            metadata.SizeOfCommands = reader.UInt32();
            metadata.Flags = reader.UInt32();
            var tableStart = (ulong)headerSize;
            context.CheckRange(tableStart, metadata.SizeOfCommands, 20);
            var commandCount = context.CheckTable(tableStart, count, 8, 8, 16);
            if (count > metadata.SizeOfCommands / 8)
            {
                throw new ContentFormatException(ErrorMessages.InvalidTable, ContentFormatErrorCode.InvalidStructure, context.Origin + 16);
            }
            // The common table check charged eight bytes per command. Charge the remaining payload budget.
            context.Reserve(metadata.SizeOfCommands - count * 8UL, 20);
            var commands = new MachOLoadCommand[commandCount];
            var end = tableStart + metadata.SizeOfCommands;
            var offset = tableStart;
            for (var i = 0; i < commands.Length; i++)
            {
                if (end - offset < 8)
                {
                    throw new ContentFormatException(ErrorMessages.InvalidCommand, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)offset);
                }
                bytes = await context.ReadAtAsync(offset, 8, cancellationToken).ConfigureAwait(false);
                reader = new EndianReader(bytes.Span, context.Origin + (long)offset, big);
                var command = new MachOLoadCommand { Command = reader.UInt32(), Size = reader.UInt32(), Offset = offset };
                if (command.Size < 8 || command.Size % (is64 ? 8 : 4) != 0)
                {
                    throw new ContentFormatException(ErrorMessages.InvalidCommand, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)offset + 4);
                }
                context.CheckRange(offset, command.Size, offset + 4);
                if (command.Size > end - offset)
                {
                    throw new ContentFormatException(ErrorMessages.InvalidCommand, ContentFormatErrorCode.InvalidStructure, context.Origin + (long)offset + 4);
                }
                commands[i] = command;
                offset += command.Size;
            }
            if (offset != end)
            {
                throw new ContentFormatException(ErrorMessages.InvalidCommand, ContentFormatErrorCode.InvalidStructure, context.Origin + 20);
            }
            metadata.LoadCommands = Array.AsReadOnly(commands);
        }
        return new ExecutableFile
        {
            Format = MachOExecutableReader.Format, Machine = metadata.CpuType,
            Bitness = fat ? 0 : is64 ? 64 : 32,
            ByteOrder = big ? ExecutableByteOrder.BigEndian : ExecutableByteOrder.LittleEndian,
            Kind = metadata.FileType switch { 1 => ExecutableKind.Object, 2 => ExecutableKind.Executable, 4 => ExecutableKind.Core, 6 or 8 or 9 => ExecutableKind.Library, _ => ExecutableKind.Unknown },
            MachO = metadata
        };
    }
}
