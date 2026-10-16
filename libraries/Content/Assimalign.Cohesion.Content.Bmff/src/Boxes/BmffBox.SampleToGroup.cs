using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SampleToGroup box; ISO/IEC 14496-12 8.9.2.</summary>
public sealed class SampleToGroupBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SampleToGroupBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SampleToGroupBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SampleToGroup;

    /// <summary>Gets or sets the group type.</summary>
    public FourCC GroupingType { get; set; }
    /// <summary>Gets or sets the grouping type parameter in v1.</summary>
    public uint GroupingTypeParameter { get; set; }
    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the number of consecutive samples.</summary>
        public uint SampleCount { get; set; }
        /// <summary>Gets or sets the group description index.</summary>
        public uint GroupDescriptionIndex { get; set; }
    }
    /// <summary>Gets or sets the ordered entries.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);
        GroupingType = FourCC.FromUInt32(BmffFields.U32(stream));
        if (Version == 1)
        {
            GroupingTypeParameter = BmffFields.U32(stream);
        }

        var count = BmffFields.Count(stream, BmffFields.U32(stream), 8, 8);
        Entries = new Entry[count];
        for (var index = 0; index < count; index++)
        {
            Entries[index] = new Entry
            {
                SampleCount = BmffFields.U32(stream),
                GroupDescriptionIndex = BmffFields.U32(stream)
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
        BmffFields.U32(stream, GroupingType.Value);
        if (Version == 1)
        {
            BmffFields.U32(stream, GroupingTypeParameter);
        }

        BmffFields.U32(stream, (uint)Entries.Length);
        foreach (var value in Entries)
        {
            BmffFields.U32(stream, value.SampleCount);
            BmffFields.U32(stream, value.GroupDescriptionIndex);
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return (Version == 1 ? 16L : 12L) + (8) * (long)Entries.Length + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
