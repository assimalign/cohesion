using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The EditList box; ISO/IEC 14496-12 8.6.6.</summary>
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class EditListBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a EditListBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public EditListBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.EditList;

    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the edit duration in movie timescale units.</summary>
        public ulong SegmentDuration { get; set; }
        /// <summary>Gets or sets the media time, or -1 for an empty edit.</summary>
        public long MediaTime { get; set; }
        /// <summary>Gets or sets the integer part of the media rate.</summary>
        public short MediaRateInteger { get; set; }
        /// <summary>Gets or sets the fractional part of the media rate.</summary>
        public short MediaRateFraction { get; set; }
    }
    /// <summary>Gets or sets the ordered entries.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);

        var count = BmffFields.Count(stream, BmffFields.U32(stream), (Version == 1 ? 20 : 12), 24);
        Entries = new Entry[count];
        for (var index = 0; index < count; index++)
        {
            Entries[index] = new Entry
            {
                SegmentDuration = BmffFields.Sized(stream, Version == 1 ? 8 : 4),
                MediaTime = Version == 1 ? BmffFields.I64(stream) : BmffFields.I32(stream),
                MediaRateInteger = BmffFields.I16(stream),
                MediaRateFraction = BmffFields.I16(stream)
            };
        }
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);

        BmffFields.U32(stream, (uint)Entries.Length);
        foreach (var value in Entries)
        {
            BmffFields.Sized(stream, value.SegmentDuration, Version == 1 ? 8 : 4);
            if (Version == 1)
            {
                BmffFields.I64(stream, value.MediaTime);
            }
            else
            {
                BmffFields.I32(stream, (int)value.MediaTime);
            }

            BmffFields.I16(stream, value.MediaRateInteger);
            BmffFields.I16(stream, value.MediaRateFraction);
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 8L + ((Version == 1 ? 20 : 12)) * (long)Entries.Length + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        foreach (var value in Entries)
        {
            BmffFields.Width(value.SegmentDuration, Version == 1 ? 8 : 4, Offset);
            if (value.MediaTime < -1 || (Version == 0 && value.MediaTime > int.MaxValue))
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" entries={Entries.Length}"));
    }

    private sealed class DebugView
    {
        private readonly EditListBox _target;

        public DebugView(EditListBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public Entry[] Entries => _target.Entries;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
