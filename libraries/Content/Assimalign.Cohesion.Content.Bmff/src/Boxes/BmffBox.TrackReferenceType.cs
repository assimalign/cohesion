using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The TrackReference box; ISO/IEC 14496-12 8.3.3.</summary>
public sealed class TrackReferenceTypeBox : BmffBox
{
    private byte[] _trailing = Array.Empty<byte>();
    private readonly BmffBoxType _type;
    private TrackReferenceTypeBox(BmffBoxType type) { _type = type; }
    internal TrackReferenceTypeBox(BmffBoxHeader header) { _type = header.BoxType; Header = header; }
    /// <summary>Creates a track-reference type box for use inside tref.</summary>
    /// <param name="type">The track-reference FourCC.</param>
    /// <returns>The empty reference list.</returns>
    public static TrackReferenceTypeBox Create(FourCC type) => new TrackReferenceTypeBox(type.ToBmffBoxType());

    /// <inheritdoc/>
    public override BmffBoxType BoxType => _type;

    /// <summary>Gets or sets the referenced track identifiers.</summary>
    public uint[] TrackIds { get; set; } = Array.Empty<uint>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        var count = BmffFields.Count(stream, (ulong)(stream.Remaining / 4), 4, 4);
        TrackIds = new uint[count];
        for (var i = 0; i < count; i++)
        {
            TrackIds[i] = BmffFields.U32(stream);
        }

        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();

        foreach (var value in TrackIds)
        {
            BmffFields.U32(stream, value);
        }

        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 4L * TrackIds.Length + _trailing.Length;
    }
    private void Validate()
    {
        ArgumentNullException.ThrowIfNull(TrackIds);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" track-ids={TrackIds.Length}"));
    }
}
