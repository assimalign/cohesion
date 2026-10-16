using System;
using System.Buffers.Binary;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// Original progressive fixture with ISO/IEC 14496-12 8.16.3 and 8.7.9 field vectors.
internal static class Mp4IndexFixtures
{
    internal static byte[] Create(bool wide)
    {
        var brand = Mp4Fixtures.Brand("mp42");
        var sidx = new byte[wide ? 44 : 36];
        sidx[0] = wide ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(sidx.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(sidx.AsSpan(8), 1000);
        var count = wide ? 30 : 22;
        sidx[count + 1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(sidx.AsSpan(count + 2), 24);
        BinaryPrimitives.WriteUInt32BigEndian(sidx.AsSpan(count + 6), 4000);
        BinaryPrimitives.WriteUInt32BigEndian(sidx.AsSpan(count + 10), 0x90000000);
        var dataOffset = (uint)(Mp4Fixtures.Write(brand).Length + sidx.Length + 16);
        var track = Mp4MediaFixtures.Track(1, true, dataOffset);
        var saio = new byte[wide ? 24 : 20];
        saio[0] = wide ? (byte)1 : (byte)0;
        saio[3] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(saio.AsSpan(4), 0x63656e63);
        BinaryPrimitives.WriteUInt32BigEndian(saio.AsSpan(12), 1);
        if (wide) { BinaryPrimitives.WriteUInt64BigEndian(saio.AsSpan(16), dataOffset + 12); }
        else { BinaryPrimitives.WriteUInt32BigEndian(saio.AsSpan(16), dataOffset + 12); }
        Mp4MediaFixtures.Find<SampleTableBox>(track).Children.Add(Mp4Fixtures.Opaque("saio", saio));
        var movie = Mp4Fixtures.Container(new MovieBox(0, 0), new MovieHeaderBox(0, 0) { TimeScale = 1000, Duration = 4000 }, track);
        return Mp4Fixtures.Write(brand, Mp4Fixtures.Opaque("sidx", sidx),
            new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0xa1, 0xb2, 0xc3, 0xd4 }) }, movie);
    }
}
