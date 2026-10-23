using System;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// ISO/IEC 14496-12 8.16.3. Internal until a public field model/typed visitor is authorized.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffSegmentIndexBox : BmffFullBox
{
    private byte[] _tail = Array.Empty<byte>();
    internal uint ReferenceId { get; set; }
    internal uint Timescale { get; set; }
    internal ulong EarliestPresentationTime { get; set; }
    internal ulong FirstOffset { get; set; }
    internal ushort Reserved { get; set; }
    internal record struct Entry(uint Reference, uint Duration, uint Sap);
    internal Entry[] Entries { get; set; } = Array.Empty<Entry>();
    public override BmffBoxType BoxType => (BmffBoxType)0x73696478;
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);
        ReferenceId = BmffFields.U32(stream);
        Timescale = BmffFields.U32(stream);
        EarliestPresentationTime = BmffFields.Sized(stream, Version == 0 ? 4 : 8);
        FirstOffset = BmffFields.Sized(stream, Version == 0 ? 4 : 8);
        Reserved = BmffFields.U16(stream);
        var count = BmffFields.Count(stream, BmffFields.U16(stream), 12, 12);
        Entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            Entries[i] = new Entry(BmffFields.U32(stream), BmffFields.U32(stream), BmffFields.U32(stream));
        }
        _tail = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, ReferenceId);
        BmffFields.U32(stream, Timescale);
        BmffFields.Sized(stream, EarliestPresentationTime, Version == 0 ? 4 : 8);
        BmffFields.Sized(stream, FirstOffset, Version == 0 ? 4 : 8);
        BmffFields.U16(stream, Reserved);
        BmffFields.U16(stream, (ushort)Entries.Length);
        foreach (var entry in Entries)
        {
            BmffFields.U32(stream, entry.Reference);
            BmffFields.U32(stream, entry.Duration);
            BmffFields.U32(stream, entry.Sap);
        }
        stream.Write(_tail);
    }
    public override long GetPayloadSize() { Validate(); return (Version == 0 ? 24L : 32L) + Entries.LongLength * 12 + _tail.Length; }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        BmffFields.Width(EarliestPresentationTime, Version == 0 ? 4 : 8, Offset);
        BmffFields.Width(FirstOffset, Version == 0 ? 4 : 8, Offset);
        if (Timescale == 0 || Entries.Length > ushort.MaxValue)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }

    private sealed class DebugView
    {
        private readonly BmffSegmentIndexBox _target;

        public DebugView(BmffSegmentIndexBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
