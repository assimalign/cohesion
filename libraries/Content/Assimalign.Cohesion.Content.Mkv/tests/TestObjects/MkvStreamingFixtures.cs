using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

// License-clean streams emitted by our EBML writer using the in-code block fixtures.
internal static class MkvStreamingFixtures
{
    internal static byte[] EncodeBlock(byte[] payload, bool simple = true)
    {
        using var output = new MemoryStream();
        using var writer = EbmlWriter.Create(output, MkvSchema.Matroska);
        writer.WriteBinary(VInt.FromEncoded(simple ? 0xA3UL : 0xA1UL), BinarySegment.Create(payload));
        return output.ToArray();
    }

    internal static List<MkvFrameSnapshot> Frames(Stream source, bool streaming)
    {
        using var reader = EbmlReader.Create(source, registry: MkvSchema.Registry,
            options: new EbmlReaderOptions { MaxBufferedValueLength = 64 });
        var frames = new List<MkvFrameSnapshot>();
        Walk();
        return frames;

        void Walk()
        {
            while (reader.ReadNext())
            {
                var element = reader.Current;
                if (element.Identifier.EncodedValue is 0xA3 or 0xA1)
                {
                    if (streaming)
                    {
                        using var block = MkvBlockReader.Create(reader);
                        var index = 0;
                        while (block.ReadFrame() is { } frame)
                        {
                            using (frame)
                            {
                                using var bytes = new MemoryStream();
                                frame.CopyTo(bytes);
                                frames.Add(new MkvFrameSnapshot(block.TrackNumber, block.RelativeTimestamp, block.Lacing,
                                    block.IsKeyframe, block.IsInvisible, block.IsDiscardable, block.FrameOffsets[index++], bytes.ToArray()));
                            }
                        }
                    }
                    else
                    {
                        var block = MkvBlock.Parse(reader.ReadBinary(), element.DataOffset, element.Identifier.EncodedValue == 0xA3);
                        for (var i = 0; i < block.Frames.Count; i++)
                        {
                            frames.Add(new MkvFrameSnapshot(block.TrackNumber, block.RelativeTimestamp, block.Lacing,
                                block.IsKeyframe, block.IsInvisible, block.IsDiscardable, block.FrameOffsets[i], block.Frames[i].ToArray(4096)));
                        }
                    }
                }
                else if (element.Type == EbmlElementType.MasterElement) { reader.EnterMaster(); Walk(); reader.LeaveMaster(); }
                else { reader.Skip(); }
            }
        }
    }
}

internal sealed record MkvFrameSnapshot(ulong Track, short Timestamp, MkvLacing Lacing, bool Keyframe,
    bool Invisible, bool Discardable, long Offset, byte[] Bytes);
