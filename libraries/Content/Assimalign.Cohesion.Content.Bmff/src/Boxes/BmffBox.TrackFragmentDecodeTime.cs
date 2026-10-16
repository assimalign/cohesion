using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The TrackFragmentDecodeTime box; ISO/IEC 14496-12 8.8.12.</summary>
public sealed class TrackFragmentDecodeTimeBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates an empty TrackFragmentDecodeTimeBox.</summary>
    public TrackFragmentDecodeTimeBox() { }
    internal TrackFragmentDecodeTimeBox(long offset, long limit) { SetOriginalLocation(offset, limit); }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.TrackFragmentDecodeTime;

    /// <summary>Gets or sets the time in track/movie timescale units.</summary>
    public ulong BaseMediaDecodeTime { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);
        BaseMediaDecodeTime = Version == 1 ? BmffFields.U64(stream) : BmffFields.U32(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.Sized(stream, BaseMediaDecodeTime, Version == 1 ? 8 : 4);
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
        BmffFields.Width(BaseMediaDecodeTime, Version == 1 ? 8 : 4, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
