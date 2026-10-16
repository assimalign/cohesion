using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The MovieExtensionHeader box; ISO/IEC 14496-12 8.8.2.</summary>
public sealed class MovieExtensionHeaderBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a MovieExtensionHeaderBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public MovieExtensionHeaderBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.MovieExtensionHeader;

    /// <summary>Gets or sets the time in track/movie timescale units.</summary>
    public ulong FragmentDuration { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);
        FragmentDuration = Version == 1 ? BmffFields.U64(stream) : BmffFields.U32(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.Sized(stream, FragmentDuration, Version == 1 ? 8 : 4);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return (Version == 1 ? 12L : 8L) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        BmffFields.Width(FragmentDuration, Version == 1 ? 8 : 4, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
