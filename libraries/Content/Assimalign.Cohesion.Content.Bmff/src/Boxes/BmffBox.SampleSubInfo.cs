using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SubSampleInformation box; ISO/IEC 14496-12 8.7.7.</summary>
public sealed class SampleSubInfoBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SampleSubInfoBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SampleSubInfoBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SubSampleInformation;

    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Subsample
    {
        /// <summary>Gets or sets the subsample size.</summary>
        public uint Size { get; set; }
        /// <summary>Gets or sets the subsample priority.</summary>
        public byte Priority { get; set; }
        /// <summary>Gets or sets the discardability indicator.</summary>
        public byte Discardable { get; set; }
        /// <summary>Gets or sets the codec-specific parameter bits.</summary>
        public uint CodecSpecificParameters { get; set; }
    }
    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the sample number delta.</summary>
        public uint SampleDelta { get; set; }
        /// <summary>Gets or sets the subsample records.</summary>
        public Subsample[] Subsamples { get; set; }
    }
    /// <summary>Gets or sets the subsample entries.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();
    private long EntryBytes()
    {
        long size = 0;
        foreach (var value in Entries)
        {
            size += 6L + (Version == 1 ? 10L : 8L) * value.Subsamples.Length;
        }

        return size;
    }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0xffffff, stream.Offset);
        var count = BmffFields.Count(stream, BmffFields.U32(stream), 6, 16);
        Entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var delta = BmffFields.U32(stream);
            var subCount = BmffFields.Count(stream, BmffFields.U16(stream), Version == 1 ? 10 : 8, 12);
            var subsamples = new Subsample[subCount];
            for (var j = 0; j < subCount; j++)
            {
                subsamples[j] = new Subsample { Size = Version == 1 ? BmffFields.U32(stream) : BmffFields.U16(stream), Priority = BmffFields.U8(stream), Discardable = BmffFields.U8(stream), CodecSpecificParameters = BmffFields.U32(stream) };
            }

            Entries[i] = new Entry { SampleDelta = delta, Subsamples = subsamples };
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
            BmffFields.U32(stream, value.SampleDelta);
            BmffFields.U16(stream, (ushort)value.Subsamples.Length);
            foreach (var sub in value.Subsamples)
            {
                BmffFields.Sized(stream, sub.Size, Version == 1 ? 4 : 2);
                BmffFields.U8(stream, sub.Priority);
                BmffFields.U8(stream, sub.Discardable);
                BmffFields.U32(stream, sub.CodecSpecificParameters);
            }
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 8L + EntryBytes() + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0xffffff, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        foreach (var value in Entries)
        {
            ArgumentNullException.ThrowIfNull(value.Subsamples);
            if (value.Subsamples.Length > ushort.MaxValue)
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }

            foreach (var sub in value.Subsamples)
            {
                BmffFields.Width(sub.Size, Version == 1 ? 4 : 2, Offset);
            }
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
