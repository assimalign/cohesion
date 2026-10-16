using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

// License-clean container fixtures produced entirely by our EbmlWriter/VInt implementation.
// Codec bytes are deliberately placeholders, not playable or downloaded bitstreams.
internal static class MkvFixtures
{
    internal static byte[] Create(bool webm = true, bool cues = true, bool live = false, bool metadata = false, bool laces = false, int clusterCount = 3)
    {
        using var output = new MemoryStream();
        using var writer = EbmlWriter.Create(output, MkvSchema.Matroska);
        var header = new EbmlHeader { DocType = webm ? "webm" : "matroska", DocTypeVersion = 4, DocTypeReadVersion = 4 };
        writer.WriteElement(header.Element);
        writer.BeginMaster(Id(0x18538067), sizeLength: 8, backPatch: !live);
        var segmentData = writer.Position;
        var patches = new List<(long Payload, ulong Position)>();
        long infoSeek = 0, tracksSeek = 0, cuesSeek = 0;
        if (cues)
        {
            Start(0x114D9B74);
            infoSeek = Seek(0x1549A966); tracksSeek = Seek(0x1654AE6B); cuesSeek = Seek(0x1C53BB6B);
            writer.EndMaster();
        }
        var infoPosition = (ulong)(writer.Position - segmentData);
        Start(0x1549A966);
        writer.WriteUtf8String(Id(0x4D80), "Cohesion fixture writer");
        writer.WriteUtf8String(Id(0x5741), "Cohesion tests");
        writer.WriteUtf8String(Id(0x7BA9), "Fixture title");
        writer.WriteFloat(Id(0x4489), clusterCount * 1000 - 500, 8);
        writer.EndMaster();
        var tracksPosition = (ulong)(writer.Position - segmentData);
        Start(0x1654AE6B);
        Track(1, 1, "V_VP9"); Track(2, 2, "A_OPUS");
        if (metadata) { Track(3, 17, "S_TEXT/UTF8"); }
        writer.EndMaster();
        if (metadata)
        {
            Start(0x1254C367); Start(0x7373);
            Start(0x63C0); writer.EndMaster();
            Start(0x67C8); writer.WriteUtf8String(Id(0x45A3), "COMMENT"); writer.WriteUtf8String(Id(0x4487), "original tag");
            writer.EndMaster(); writer.EndMaster(); writer.EndMaster();
            Start(0x1941A469); Start(0x61A7);
            writer.WriteUtf8String(Id(0x466E), "notes.txt"); writer.WriteAsciiString(Id(0x4660), "text/plain");
            writer.WriteUnsignedInteger(Id(0x46AE), 1);
            writer.WriteBinary(Id(0x465C), BinarySegment.Create(new byte[] { 10, 20, 30, 40 }));
            writer.EndMaster(); writer.EndMaster();
            // RFC 9559 section 4.2: the old EBML core cannot construct ID 0x80 as an element.
            // Build just that header with its VInt encoder; all child values use EbmlWriter.
            writer.WriteBinary(Id(0x1043A770), BinarySegment.Create(Chapters()));
        }
        var clusterPositions = new List<ulong>();
        for (var cluster = 0; cluster < clusterCount; cluster++)
        {
            clusterPositions.Add((ulong)(writer.Position - segmentData));
            writer.BeginMaster(Id(0x1F43B675), sizeLength: 8, backPatch: !live);
            writer.WriteUnsignedInteger(Id(0xE7), (ulong)(cluster * 1000));
            writer.WriteBinary(Id(0xA3), BinarySegment.Create(Block(1, 0, 0x80, new byte[] { (byte)(0x90 + cluster), 11, 12 })));
            writer.WriteBinary(Id(0xA3), BinarySegment.Create(Block(1, 500, 0, new byte[] { 0x11, 13 })));
            var audio = laces ? Laced((MkvLacing)(cluster + 1)) : Block(2, 0, 0x80, new byte[] { 0xF8, 0xFF });
            writer.WriteBinary(Id(0xA3), BinarySegment.Create(audio));
            if (metadata)
            {
                Start(0xA0);
                writer.WriteBinary(Id(0xA1), BinarySegment.Create(Block(3, 0, 0, new byte[] { 72, 105 })));
                writer.WriteUnsignedInteger(Id(0x9B), 500);
                writer.EndMaster();
            }
            writer.EndMaster();
        }
        var cuesPosition = (ulong)(writer.Position - segmentData);
        if (cues)
        {
            Start(0x1C53BB6B);
            for (var i = 0; i < clusterPositions.Count; i++)
            {
                Start(0xBB); writer.WriteUnsignedInteger(Id(0xB3), (ulong)(i * 1000)); Start(0xB7);
                writer.WriteUnsignedInteger(Id(0xF7), 1); writer.WriteUnsignedInteger(Id(0xF1), clusterPositions[i]);
                writer.EndMaster(); writer.EndMaster();
            }
            writer.EndMaster();
        }
        writer.EndMaster();
        if (cues)
        {
            patches.Add((infoSeek, infoPosition)); patches.Add((tracksSeek, tracksPosition)); patches.Add((cuesSeek, cuesPosition));
            Span<byte> number = stackalloc byte[8];
            foreach (var patch in patches)
            { output.Position = patch.Payload; BinaryPrimitives.WriteUInt64BigEndian(number, patch.Position); output.Write(number); }
        }
        return output.ToArray();

        void Start(ulong id) => writer.BeginMaster(Id(id), sizeLength: 8, backPatch: true);
        long Seek(uint id)
        {
            Start(0x4DBB);
            var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, id);
            writer.WriteBinary(Id(0x53AB), BinarySegment.Create(bytes));
            var position = writer.Position + 3;
            writer.WriteUnsignedInteger(Id(0x53AC), 0, 8, 1);
            writer.EndMaster(); return position;
        }
        void Track(ulong number, ulong type, string codec)
        {
            Start(0xAE);
            writer.WriteUnsignedInteger(Id(0xD7), number); writer.WriteUnsignedInteger(Id(0x73C5), number + 100);
            writer.WriteUnsignedInteger(Id(0x83), type); writer.WriteAsciiString(Id(0x86), codec);
            writer.WriteAsciiString(Id(0x22B59C), "eng", 3);
            writer.WriteUtf8String(Id(0x536E), "track " + number);
            writer.WriteBinary(Id(0x63A2), BinarySegment.Create(new byte[] { 1, 2, 3 }));
            if (type == 1)
            { Start(0xE0); writer.WriteUnsignedInteger(Id(0xB0), 640); writer.WriteUnsignedInteger(Id(0xBA), 360); writer.EndMaster(); }
            if (type == 2)
            {
                writer.WriteUnsignedInteger(Id(0x23E383), 20_000_000);
                writer.WriteUnsignedInteger(Id(0x56AA), 6_500_000);
                writer.WriteUnsignedInteger(Id(0x56BB), 80_000_000);
                Start(0xE1); writer.WriteFloat(Id(0xB5), 48_000, 8); writer.WriteUnsignedInteger(Id(0x9F), 2); writer.EndMaster();
            }
            writer.EndMaster();
        }
    }

    internal static byte[] Block(byte track, short relative, byte flags, byte[] frames)
    {
        var result = new byte[frames.Length + 4];
        result[0] = (byte)(0x80 | track); BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(1), relative); result[3] = flags;
        frames.CopyTo(result, 4); return result;
    }
    internal static byte[] Laced(MkvLacing mode) => mode switch
    {
        MkvLacing.Xiph => Block(2, 0, 0x82, new byte[] { 2, 2, 3, 1, 2, 3, 4, 5, 6 }),
        MkvLacing.FixedSize => Block(2, 0, 0x84, new byte[] { 2, 1, 2, 3, 4, 5, 6 }),
        MkvLacing.Ebml => Block(2, 0, 0x86, new byte[] { 2, 0x82, 0xC0, 1, 2, 3, 4, 5, 6 }),
        _ => throw new InvalidOperationException()
    };
    private static byte[] Chapters()
    {
        using var display = new MemoryStream();
        using (var strings = EbmlWriter.Create(display)) { strings.WriteUtf8String(Id(0x85), "Introduction"); }
        using var atom = new MemoryStream();
        using (var writer = EbmlWriter.Create(atom)) { writer.WriteUnsignedInteger(Id(0x73C4), 1); writer.WriteUnsignedInteger(Id(0x91), 0); }
        Id(0x80).Write(atom); VInt.EncodeSize((ulong)display.Length).Write(atom); display.Position = 0; display.CopyTo(atom);
        using var edition = new MemoryStream();
        using (var writer = EbmlWriter.Create(edition)) { writer.WriteBinary(Id(0xB6), BinarySegment.Create(atom.ToArray())); }
        using var chapters = new MemoryStream();
        using (var writer = EbmlWriter.Create(chapters)) { writer.WriteBinary(Id(0x45B9), BinarySegment.Create(edition.ToArray())); }
        return chapters.ToArray();
    }
    internal static VInt Id(ulong value) => VInt.FromEncoded(value);
    internal static EbmlElement Find(EbmlElement parent, ulong id)
    {
        foreach (var child in parent.Children) { if (child.Identifier.EncodedValue == id) { return child; } }
        throw new InvalidOperationException();
    }
}
