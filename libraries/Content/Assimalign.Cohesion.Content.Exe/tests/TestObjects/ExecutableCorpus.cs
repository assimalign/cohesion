using System;
using System.Text;

using static Assimalign.Cohesion.Content.Exe.Tests.ExecutableHeaders;

namespace Assimalign.Cohesion.Content.Exe.Tests;

// Constructed from specification layouts, never downloaded. Payloads are inert zeros/text.
internal static class ExecutableCorpus
{
    internal static byte[] PeImage(bool is64)
    {
        var header = Pe(is64);
        var fixedSize = is64 ? 112 : 96;
        var sectionOffset = header.Length + 16 * 8;
        var payloadOffset = sectionOffset + 40;
        var data = new byte[payloadOffset + 128];
        header.CopyTo(data, 0);
        Put16(data, 70, 1);
        Put16(data, 84, (ushort)(fixedSize + 16 * 8));
        Put32(data, 148, (uint)payloadOffset);
        Put32(data, 88 + fixedSize - 4, 16);
        Put32(data, header.Length + 14 * 8, 0x2000);
        Put32(data, header.Length + 14 * 8 + 4, 72);
        ".text"u8.CopyTo(data.AsSpan(sectionOffset));
        Put32(data, sectionOffset + 8, 128);
        Put32(data, sectionOffset + 12, 0x2000);
        Put32(data, sectionOffset + 16, 128);
        Put32(data, sectionOffset + 20, (uint)payloadOffset);
        Put32(data, sectionOffset + 36, 0x60000020);
        return data;
    }

    internal static byte[] ElfImage(bool is64, bool big = false, bool extended = false)
    {
        var header = Elf(is64, big);
        var programSize = is64 ? 56 : 32;
        var sectionSize = is64 ? 64 : 40;
        var phOffset = header.Length;
        var shOffset = phOffset + programSize;
        var nameOffset = shOffset + 3 * sectionSize;
        var names = Encoding.ASCII.GetBytes("\0.shstrtab\0.text\0");
        var interpreter = Encoding.ASCII.GetBytes("/lib/test-loader\0");
        var interpreterOffset = nameOffset + names.Length;
        var payloadOffset = interpreterOffset + interpreter.Length;
        var data = new byte[payloadOffset + 4];
        header.CopyTo(data, 0);
        Word(data, is64 ? 32 : 28, (ulong)phOffset, is64, big);
        Word(data, is64 ? 40 : 32, (ulong)shOffset, is64, big);
        var tail = is64 ? 52 : 40;
        Put16(data, tail + 2, (ushort)programSize, big);
        Put16(data, tail + 4, extended ? ushort.MaxValue : (ushort)1, big);
        Put16(data, tail + 6, (ushort)sectionSize, big);
        Put16(data, tail + 8, extended ? (ushort)0 : (ushort)3, big);
        Put16(data, tail + 10, extended ? ushort.MaxValue : (ushort)1, big);
        if (extended)
        {
            Word(data, shOffset + (is64 ? 32 : 20), 3, is64, big);
            Put32(data, shOffset + (is64 ? 40 : 24), 1, big);
            Put32(data, shOffset + (is64 ? 44 : 28), 1, big);
        }
        Put32(data, phOffset, 3, big); // PT_INTERP.
        if (is64) { Put32(data, phOffset + 4, 4, big); }
        Word(data, phOffset + (is64 ? 8 : 4), (ulong)interpreterOffset, is64, big);
        Word(data, phOffset + (is64 ? 32 : 16), (ulong)interpreter.Length, is64, big);
        Word(data, phOffset + (is64 ? 40 : 20), (ulong)interpreter.Length, is64, big);
        if (!is64) { Put32(data, phOffset + 24, 4, big); }
        Word(data, phOffset + (is64 ? 48 : 28), 1, is64, big);
        Section(data, shOffset + sectionSize, 1, 3, (ulong)nameOffset, (ulong)names.Length, is64, big);
        Section(data, shOffset + 2 * sectionSize, 11, 1, (ulong)payloadOffset, 4, is64, big);
        names.CopyTo(data, nameOffset);
        interpreter.CopyTo(data, interpreterOffset);
        return data;
    }

    internal static byte[] MachOImage(bool is64, bool big = false)
    {
        var header = MachO(is64, big);
        var data = new byte[header.Length + 32];
        header.CopyTo(data, 0);
        Put32(data, 16, 2, big);
        Put32(data, 20, 32, big);
        Put32(data, header.Length, 0x80000028, big);
        Put32(data, header.Length + 4, 24, big);
        Put32(data, header.Length + 24, 0x12345678, big);
        Put32(data, header.Length + 28, 8, big);
        return data;
    }

    internal static byte[] FatMachO(bool is64 = false, bool big = true)
    {
        var stride = is64 ? 32 : 20;
        var firstOffset = is64 ? 80 : 48;
        var secondOffset = firstOffset + 32;
        var data = new byte[secondOffset + 32];
        Put32(data, 0, is64 ? 0xCAFEBABFU : 0xCAFEBABEU, big);
        Put32(data, 4, 2, big);
        for (var i = 0; i < 2; i++)
        {
            var offset = 8 + i * stride;
            var memberOffset = i == 0 ? firstOffset : secondOffset;
            Put32(data, offset, i == 0 ? 0x1000007U : 0x100000CU, big);
            Put32(data, offset + 4, 3, big);
            Word(data, offset + 8, (ulong)memberOffset, is64, big);
            Word(data, offset + (is64 ? 16 : 12), 32, is64, big);
            Put32(data, offset + (is64 ? 24 : 16), 4, big);
            MachO(true).CopyTo(data, memberOffset);
            Put32(data, memberOffset + 4, i == 0 ? 0x1000007U : 0x100000CU);
        }
        return data;
    }

    private static void Word(byte[] data, int offset, ulong value, bool is64, bool big)
    {
        if (is64) { Put64(data, offset, value, big); }
        else { Put32(data, offset, (uint)value, big); }
    }

    private static void Section(byte[] data, int offset, uint name, uint type, ulong dataOffset, ulong size, bool is64, bool big)
    {
        Put32(data, offset, name, big);
        Put32(data, offset + 4, type, big);
        Word(data, offset + (is64 ? 24 : 16), dataOffset, is64, big);
        Word(data, offset + (is64 ? 32 : 20), size, is64, big);
        Word(data, offset + (is64 ? 48 : 32), 1, is64, big);
    }
}
