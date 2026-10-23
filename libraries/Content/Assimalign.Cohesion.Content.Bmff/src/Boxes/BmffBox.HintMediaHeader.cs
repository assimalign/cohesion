using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The HintMediaHeader box; ISO/IEC 14496-12 8.4.5.4.</summary>
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HintMediaHeaderBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a HintMediaHeaderBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public HintMediaHeaderBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.HintMediaHeader;

    /// <summary>Gets or sets the largest PDU size.</summary>
    public ushort MaxPduSize { get; set; }
    /// <summary>Gets or sets the average PDU size.</summary>
    public ushort AveragePduSize { get; set; }
    /// <summary>Gets or sets the maximum bitrate.</summary>
    public uint MaxBitrate { get; set; }
    /// <summary>Gets or sets the average bitrate.</summary>
    public uint AverageBitrate { get; set; }
    /// <summary>Gets or sets the reserved bits, retained verbatim.</summary>
    public uint Reserved { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        MaxPduSize = BmffFields.U16(stream);
        AveragePduSize = BmffFields.U16(stream);
        MaxBitrate = BmffFields.U32(stream);
        AverageBitrate = BmffFields.U32(stream);
        Reserved = BmffFields.U32(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U16(stream, MaxPduSize);
        BmffFields.U16(stream, AveragePduSize);
        BmffFields.U32(stream, MaxBitrate);
        BmffFields.U32(stream, AverageBitrate);
        BmffFields.U32(stream, Reserved);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 20L + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" max-bitrate={MaxBitrate} average-bitrate={AverageBitrate}"));
    }

    private sealed class DebugView
    {
        private readonly HintMediaHeaderBox _target;

        public DebugView(HintMediaHeaderBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public ushort MaxPduSize => _target.MaxPduSize;
        public ushort AveragePduSize => _target.AveragePduSize;
        public uint MaxBitrate => _target.MaxBitrate;
        public uint AverageBitrate => _target.AverageBitrate;
        public uint Reserved => _target.Reserved;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
