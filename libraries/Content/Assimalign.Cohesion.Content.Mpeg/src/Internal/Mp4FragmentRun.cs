using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal readonly struct Mp4FragmentRun
{
    internal Mp4FragmentRun(TrackFragmentRunBox box, long index, long offset, long decodeTime,
        uint duration, uint size, uint flags, long moofOffset, long moofEnd)
    {
        Box = box;
        Index = index;
        Offset = offset;
        DecodeTime = decodeTime;
        Duration = duration;
        Size = size;
        Flags = flags;
        MoofOffset = moofOffset;
        MoofEnd = moofEnd;
    }
    internal TrackFragmentRunBox Box { get; }
    internal long Index { get; }
    internal long Offset { get; }
    internal long DecodeTime { get; }
    internal uint Duration { get; }
    internal uint Size { get; }
    internal uint Flags { get; }
    internal long MoofOffset { get; }
    internal long MoofEnd { get; }
    internal uint SampleDuration(int index) => (Box.Flags & 0x100) != 0 ? Box.Entries[index].Duration : Duration;
    internal uint SampleSize(int index) => (Box.Flags & 0x200) != 0 ? Box.Entries[index].Size : Size;
    internal long CompositionOffset(int index) => (Box.Flags & 0x800) != 0 ? Box.Entries[index].CompositionTimeOffset : 0;
    internal bool IsSync(int index)
    {
        var flags = (Box.Flags & 0x400) != 0 ? Box.Entries[index].Flags :
            index == 0 && Box.FirstSampleFlags.HasValue ? Box.FirstSampleFlags.Value : Flags;
        return (flags & 0x10000) == 0 && ((flags >> 24) & 3) != 1;
    }
}
