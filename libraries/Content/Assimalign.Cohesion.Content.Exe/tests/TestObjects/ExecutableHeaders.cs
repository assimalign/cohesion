using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Content.Exe.Tests;

// License-clean fixtures synthesized from the PE/COFF, ELF gABI and Apple header layouts.
internal static class ExecutableHeaders
{
    internal static byte[] Pe(bool is64)
    {
        var data = new byte[88 + (is64 ? 112 : 96)];
        Put16(data, 0, 0x5A4D);
        Put32(data, 60, 64);
        Put32(data, 64, 0x4550);
        Put16(data, 68, is64 ? (ushort)0x8664 : (ushort)0x14C);
        Put32(data, 72, 123456);
        Put16(data, 84, (ushort)(data.Length - 88));
        Put16(data, 86, 0x2002);
        Put16(data, 88, is64 ? (ushort)0x20B : (ushort)0x10B);
        Put32(data, 92, 0x200);
        Put32(data, 96, 0x400);
        Put32(data, 100, 0x600);
        Put32(data, 104, 0x1234);
        if (is64)
        {
            Put64(data, 112, 0x140000000);
        }
        else
        {
            Put32(data, 116, 0x400000);
        }
        Put32(data, 120, 0x1000);
        Put32(data, 124, 0x200);
        Put32(data, 144, 0x3000);
        Put32(data, 148, (uint)data.Length);
        Put16(data, 156, 3);
        Put16(data, 158, 0x140);
        return data;
    }

    internal static byte[] Elf(bool is64, bool big = false, ushort type = 2)
    {
        var data = new byte[is64 ? 64 : 52];
        data[0] = 0x7F;
        data[1] = (byte)'E';
        data[2] = (byte)'L';
        data[3] = (byte)'F';
        data[4] = (byte)(is64 ? 2 : 1);
        data[5] = (byte)(big ? 2 : 1);
        data[6] = 1;
        data[7] = 3;
        data[8] = 1;
        Put16(data, 16, type, big);
        Put16(data, 18, is64 ? (ushort)62 : (ushort)3, big);
        Put32(data, 20, 1, big);
        if (is64)
        {
            Put64(data, 24, 0x1234567890, big);
        }
        else
        {
            Put32(data, 24, 0x12345678, big);
        }
        Put16(data, is64 ? 52 : 40, (ushort)data.Length, big);
        return data;
    }

    internal static byte[] MachO(bool is64, bool big = false)
    {
        var data = new byte[is64 ? 32 : 28];
        Put32(data, 0, is64 ? 0xFEEDFACFU : 0xFEEDFACEU, big);
        Put32(data, 4, is64 ? 0x1000007U : 7U, big);
        Put32(data, 8, 3, big);
        Put32(data, 12, 2, big);
        Put32(data, 24, 0x200085, big);
        return data;
    }

    internal static void Put16(byte[] data, int offset, ushort value, bool big = false)
    {
        if (big) { BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value); }
        else { BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value); }
    }

    internal static void Put32(byte[] data, int offset, uint value, bool big = false)
    {
        if (big) { BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value); }
        else { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value); }
    }

    internal static void Put64(byte[] data, int offset, ulong value, bool big = false)
    {
        if (big) { BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset), value); }
        else { BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value); }
    }
}
