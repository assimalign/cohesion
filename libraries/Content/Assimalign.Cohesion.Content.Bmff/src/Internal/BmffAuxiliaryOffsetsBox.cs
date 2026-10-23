using System;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// ISO/IEC 14496-12 8.7.9. Uses the existing generic visitor fallback.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffAuxiliaryOffsetsBox : BmffFullBox
{
    private byte[] _tail = Array.Empty<byte>();
    internal uint AuxiliaryType { get; set; }
    internal uint AuxiliaryParameter { get; set; }
    internal ulong[] Offsets { get; set; } = Array.Empty<ulong>();
    public override BmffBoxType BoxType => (BmffBoxType)0x7361696f;
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 1, stream.Offset);
        if ((Flags & 1) != 0) { AuxiliaryType = BmffFields.U32(stream); AuxiliaryParameter = BmffFields.U32(stream); }
        var count = BmffFields.Count(stream, BmffFields.U32(stream), Version == 0 ? 4 : 8, 8);
        Offsets = new ulong[count];
        for (var i = 0; i < count; i++) { Offsets[i] = BmffFields.Sized(stream, Version == 0 ? 4 : 8); }
        _tail = BmffFields.Bytes(stream, stream.Remaining);
    }
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        if ((Flags & 1) != 0) { BmffFields.U32(stream, AuxiliaryType); BmffFields.U32(stream, AuxiliaryParameter); }
        BmffFields.U32(stream, (uint)Offsets.Length);
        foreach (var offset in Offsets) { BmffFields.Sized(stream, offset, Version == 0 ? 4 : 8); }
        stream.Write(_tail);
    }
    public override long GetPayloadSize() { Validate(); return 8L + ((Flags & 1) != 0 ? 8 : 0) + Offsets.LongLength * (Version == 0 ? 4 : 8) + _tail.Length; }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 1, Offset);
        foreach (var offset in Offsets) { BmffFields.Width(offset, Version == 0 ? 4 : 8, Offset); }
    }

    private sealed class DebugView
    {
        private readonly BmffAuxiliaryOffsetsBox _target;

        public DebugView(BmffAuxiliaryOffsetsBox value)
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
