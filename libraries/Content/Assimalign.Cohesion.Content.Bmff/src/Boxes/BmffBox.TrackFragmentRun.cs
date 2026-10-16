using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The TrackFragmentRun box; ISO/IEC 14496-12 8.8.8.</summary>
public sealed class TrackFragmentRunBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a TrackFragmentRunBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public TrackFragmentRunBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.TrackFragmentRun;

    /// <summary>Gets or sets the signed data offset relative to the fragment base when flag 0x1 is set.</summary>
    public int? DataOffset { get; set; }
    /// <summary>Gets or sets the first sample flags when flag 0x4 is set.</summary>
    public uint? FirstSampleFlags { get; set; }
    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the duration when flag 0x100 is set.</summary>
        public uint Duration { get; set; }
        /// <summary>Gets or sets the size when flag 0x200 is set.</summary>
        public uint Size { get; set; }
        /// <summary>Gets or sets the sample flags when flag 0x400 is set.</summary>
        public uint Flags { get; set; }
        /// <summary>Gets or sets the unsigned v0 or signed v1 offset when flag 0x800 is set.</summary>
        public long CompositionTimeOffset { get; set; }
    }
    /// <summary>Gets or sets the samples in this run.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();
    private int EntrySize => ((Flags & 0x100) != 0 ? 4 : 0) + ((Flags & 0x200) != 0 ? 4 : 0) + ((Flags & 0x400) != 0 ? 4 : 0) + ((Flags & 0x800) != 0 ? 4 : 0);

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0xf05, stream.Offset);
        var declared = BmffFields.U32(stream);
        DataOffset = (Flags & 1) != 0 ? BmffFields.I32(stream) : null;
        FirstSampleFlags = (Flags & 4) != 0 ? BmffFields.U32(stream) : null;
        var count = BmffFields.Count(stream, declared, EntrySize, 24);
        Entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            Entries[i] = new Entry
            {
                Duration = (Flags & 0x100) != 0 ? BmffFields.U32(stream) : 0,
                Size = (Flags & 0x200) != 0 ? BmffFields.U32(stream) : 0,
                Flags = (Flags & 0x400) != 0 ? BmffFields.U32(stream) : 0,
                CompositionTimeOffset = (Flags & 0x800) == 0 ? 0 : Version == 0 ? (long)BmffFields.U32(stream) : BmffFields.I32(stream)
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
        if (DataOffset.HasValue)
        {
            BmffFields.I32(stream, DataOffset.Value);
        }

        if (FirstSampleFlags.HasValue)
        {
            BmffFields.U32(stream, FirstSampleFlags.Value);
        }

        foreach (var value in Entries)
        {
            if ((Flags & 0x100) != 0)
            {
                BmffFields.U32(stream, value.Duration);
            }

            if ((Flags & 0x200) != 0)
            {
                BmffFields.U32(stream, value.Size);
            }

            if ((Flags & 0x400) != 0)
            {
                BmffFields.U32(stream, value.Flags);
            }

            if ((Flags & 0x800) != 0)
            {
                BmffFields.U32(stream, unchecked((uint)value.CompositionTimeOffset));
            }
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 8L + (DataOffset.HasValue ? 4 : 0) + (FirstSampleFlags.HasValue ? 4 : 0) + (long)EntrySize * Entries.Length + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0xf05, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        if (DataOffset.HasValue != ((Flags & 1) != 0) || FirstSampleFlags.HasValue != ((Flags & 4) != 0) || ((Flags & 0x404) == 0x404))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        if ((Flags & 0x800) != 0)
        {
            foreach (var value in Entries)
            {
                if (value.CompositionTimeOffset < (Version == 0 ? 0L : int.MinValue) || value.CompositionTimeOffset > (Version == 0 ? (long)uint.MaxValue : int.MaxValue))
                {
                    throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
                }
            }
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
