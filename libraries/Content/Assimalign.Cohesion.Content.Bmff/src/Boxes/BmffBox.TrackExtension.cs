using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The TrackExtension box; ISO/IEC 14496-12 8.8.3.</summary>
public sealed class TrackExtensionBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a TrackExtensionBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public TrackExtensionBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.TrackExtension;

    /// <summary>Gets or sets the track identifier.</summary>
    public uint TrackId { get; set; }
    /// <summary>Gets or sets the one-based default description index.</summary>
    public uint DefaultSampleDescriptionIndex { get; set; }
    /// <summary>Gets or sets the default sample duration.</summary>
    public uint DefaultSampleDuration { get; set; }
    /// <summary>Gets or sets the default sample size.</summary>
    public uint DefaultSampleSize { get; set; }
    /// <summary>Gets or sets the default sample flags.</summary>
    public uint DefaultSampleFlags { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        TrackId = BmffFields.U32(stream);
        DefaultSampleDescriptionIndex = BmffFields.U32(stream);
        DefaultSampleDuration = BmffFields.U32(stream);
        DefaultSampleSize = BmffFields.U32(stream);
        DefaultSampleFlags = BmffFields.U32(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, TrackId);
        BmffFields.U32(stream, DefaultSampleDescriptionIndex);
        BmffFields.U32(stream, DefaultSampleDuration);
        BmffFields.U32(stream, DefaultSampleSize);
        BmffFields.U32(stream, DefaultSampleFlags);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 24L + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
