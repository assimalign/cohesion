using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The Handler box; ISO/IEC 14496-12 8.4.3.</summary>
public sealed class HandlerBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a HandlerBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public HandlerBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.Handler;

    /// <summary>Gets or sets the predefined word.</summary>
    public uint PreDefined { get; set; }
    /// <summary>Gets or sets the handler FourCC.</summary>
    public FourCC HandlerType { get; set; }
    /// <summary>Gets or sets the twelve reserved bytes.</summary>
    public byte[] Reserved { get; set; } = new byte[12];
    private byte[] _name = Array.Empty<byte>();
    /// <summary>Gets or sets the handler name. Original UTF-8 bytes are preserved until assigned.</summary>
    public string Name { get => Encoding.UTF8.GetString(_name); set => _name = Encoding.UTF8.GetBytes(value); }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        PreDefined = BmffFields.U32(stream);
        HandlerType = FourCC.FromUInt32(BmffFields.U32(stream));
        Reserved = BmffFields.Bytes(stream, 12);
        _name = BmffFields.Text(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, PreDefined);
        BmffFields.U32(stream, HandlerType.Value);
        stream.Write(Reserved);
        BmffFields.Text(stream, _name);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 24L + BmffFields.TextSize(_name, Offset) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
        BmffFields.ArrayLength(Reserved, 12, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
