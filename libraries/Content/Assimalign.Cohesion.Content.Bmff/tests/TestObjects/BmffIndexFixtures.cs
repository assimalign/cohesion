using System;
using System.Buffers.Binary;
using System.IO;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

// Original vectors from ISO/IEC 14496-12 8.16.3 (sidx) and 8.7.9 (saio).
internal static class BmffIndexFixtures
{
    internal static byte[] Sidx(ulong first, uint[] references, byte version = 0)
    {
        var payload = new byte[(version == 0 ? 24 : 32) + references.Length * 12];
        payload[0] = version;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), 1000);
        if (version == 0) { BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), (uint)first); }
        else { BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(20), first); }
        var countPosition = version == 0 ? 22 : 30;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(countPosition), (ushort)references.Length);
        for (var i = 0; i < references.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(countPosition + 2 + i * 12), references[i]);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(countPosition + 6 + i * 12), 1000);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(countPosition + 10 + i * 12), 0x90000000);
        }
        return BmffFixtures.Box("sidx", payload);
    }
    internal static byte[] Saio(ulong[] offsets, byte version = 0, bool typed = false)
    {
        var start = typed ? 16 : 8;
        var width = version == 0 ? 4 : 8;
        var payload = new byte[start + width * offsets.Length];
        payload[0] = version;
        if (typed) { payload[3] = 1; BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 0x63656e63); }
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(start - 4), (uint)offsets.Length);
        for (var i = 0; i < offsets.Length; i++)
        {
            if (version == 0) { BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(start + i * width), (uint)offsets[i]); }
            else { BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(start + i * width), offsets[i]); }
        }
        return BmffFixtures.Box("saio", payload);
    }
    internal static byte[] Payload(BmffBox box) => BmffModelFixtures.Write(box).AsSpan(8).ToArray();
    internal static ulong FirstOffset(BmffBox box)
    {
        var payload = Payload(box);
        return payload[0] == 0 ? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(16)) : BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(20));
    }
    internal static ulong AuxiliaryOffset(BmffBox box)
    {
        var payload = Payload(box);
        var start = (payload[3] & 1) != 0 ? 16 : 8;
        return payload[0] == 0 ? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(start)) : BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(start));
    }
}
