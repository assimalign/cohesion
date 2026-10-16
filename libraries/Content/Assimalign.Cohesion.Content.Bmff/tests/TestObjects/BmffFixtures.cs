using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

// All fixtures are built from the ISO/IEC 14496-12 header grammar or this package's writer.
// No downloaded or developer-local media is used.
internal static class BmffFixtures
{
    internal static byte[] Box(string type, byte[]? payload = null, bool large = false, bool zero = false, Guid? uuid = null)
    {
        payload ??= Array.Empty<byte>();
        var headerSize = (large ? 16 : 8) + (type == "uuid" ? 16 : 0);
        var bytes = new byte[headerSize + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, zero ? 0 : large ? 1u : (uint)bytes.Length);
        FourCC.Parse(type).WriteTo(bytes.AsSpan(4));
        if (large)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), (ulong)bytes.Length);
        }
        if (type == "uuid")
        {
            (uuid ?? Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")).TryWriteBytes(bytes.AsSpan(large ? 16 : 8), true, out _);
        }
        payload.CopyTo(bytes, headerSize);
        return bytes;
    }

    internal static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts)
        {
            stream.Write(part);
        }
        return stream.ToArray();
    }

    internal static byte[] Minimal(int mediaSize = 257)
    {
        using var document = new BmffDocument();
        document.Boxes.Add(new FileTypeBox(0)
        {
            MajorBrand = FourCC.Parse("isom").Value,
            MinorBrand = 0x200,
            CompatableBrands = new[] { FourCC.Parse("isom").Value, FourCC.Parse("iso2").Value }
        });
        var movie = new MovieBox(0);
        var track = new TrackBox(0, 0);
        var media = new MediaBox(0);
        var info = new MediaInfoBox(0);
        info.Children.Add(new SampleTableBox(0, 0));
        media.Children.Add(info);
        track.Children.Add(media);
        movie.Children.Add(track);
        document.Boxes.Add(movie);
        var payload = new byte[mediaSize];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)index;
        }
        document.Boxes.Add(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(payload) });
        document.Boxes.Add(new FreeSpaceBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 9, 8, 7 }) });
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    internal static byte[] DepthBomb(int depth)
    {
        var bytes = Box("free");
        for (var index = 0; index < depth; index++)
        {
            bytes = Box("moov", bytes);
        }
        return bytes;
    }
}
