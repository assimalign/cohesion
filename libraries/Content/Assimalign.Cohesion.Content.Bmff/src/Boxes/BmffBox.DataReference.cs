using System;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The counted DataReference full-box container; ISO/IEC 14496-12 8.7.2.</summary>
public sealed class DataReferenceBox : BmffBoxComposite
{
    private uint _flags;
    internal override bool SupportsAsyncPlan => true;
    /// <summary>Creates an empty container at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public DataReferenceBox(long offset, long limit) { SetOriginalLocation(offset, limit); }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.DataReference;
    /// <summary>Gets or sets the encoded full-box version.</summary>
    public byte Version { get; set; }
    /// <summary>Gets or sets the 24-bit full-box flags.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value exceeds 24 bits.</exception>
    public uint Flags { get => _flags; set { ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 0xffffffu); _flags = value; } }
    /// <summary>Gets the entry count from the editable Children collection.</summary>
    public int EntryCount => Children.Count;
    internal override int PrefixSize => 8;
    internal override int? ReadPrefix(BmffStream stream)
    {
        var prefix = BmffFields.U32(stream);
        Version = (byte)(prefix >> 24);
        Flags = prefix & 0xffffff;
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        return BmffFields.Count(stream, BmffFields.U32(stream), 8, 0);
    }
    internal override void WritePrefix(BmffStream stream)
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
        BmffFields.Width((ulong)Children.Count, 4, Offset);
        BmffFields.U32(stream, ((uint)Version << 24) | Flags);
        BmffFields.Sized(stream, (ulong)Children.Count, 4);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
