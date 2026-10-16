using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The DisposableSample box; ISO/IEC 14496-12 8.6.4.</summary>
public sealed class DisposableSampleBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a DisposableSampleBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public DisposableSampleBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.DisposableSample;

    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the two is-leading bits.</summary>
        public byte IsLeading { get; set; }
        /// <summary>Gets or sets the two sample-depends-on bits.</summary>
        public byte DependsOn { get; set; }
        /// <summary>Gets or sets the two sample-is-depended-on bits.</summary>
        public byte IsDependedOn { get; set; }
        /// <summary>Gets or sets the two sample-has-redundancy bits.</summary>
        public byte HasRedundancy { get; set; }
    }
    /// <summary>Gets or sets one dependency record per sample.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        var count = BmffFields.Count(stream, (ulong)stream.Remaining, 1, 4);
        Entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var value = BmffFields.U8(stream);
            Entries[i] = new Entry { IsLeading = (byte)(value >> 6), DependsOn = (byte)((value >> 4) & 3), IsDependedOn = (byte)((value >> 2) & 3), HasRedundancy = (byte)(value & 3) };
        }
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        foreach (var value in Entries)
        {
            BmffFields.U8(stream, (byte)((value.IsLeading << 6) | (value.DependsOn << 4) | (value.IsDependedOn << 2) | value.HasRedundancy));
        }

        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 4L + Entries.LongLength + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        foreach (var value in Entries)
        {
            if (value.IsLeading > 3 || value.DependsOn > 3 || value.IsDependedOn > 3 || value.HasRedundancy > 3)
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
