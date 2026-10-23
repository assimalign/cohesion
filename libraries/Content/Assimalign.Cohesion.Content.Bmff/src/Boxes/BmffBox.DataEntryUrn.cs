using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The DataEntryUrn box; ISO/IEC 14496-12 8.7.2.</summary>
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class DataEntryUrnBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates an empty DataEntryUrnBox.</summary>
    public DataEntryUrnBox() { }
    internal DataEntryUrnBox(long offset, long limit) { SetOriginalLocation(offset, limit); }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.DataEntryUrn;

    private byte[] _name = Array.Empty<byte>();
    /// <summary>Gets or sets the resource name. Original UTF-8 bytes are preserved until assigned.</summary>
    public string Name { get => Encoding.UTF8.GetString(_name); set => _name = Encoding.UTF8.GetBytes(value); }
    private byte[] _location = Array.Empty<byte>();
    /// <summary>Gets or sets the resource location. Original UTF-8 bytes are preserved until assigned.</summary>
    public string Location { get => Encoding.UTF8.GetString(_location); set => _location = Encoding.UTF8.GetBytes(value); }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 1, stream.Offset);
        if ((Flags & 1) == 0)
        {
            _name = BmffFields.Text(stream);
            _location = BmffFields.Text(stream);
        }
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        if ((Flags & 1) == 0)
        {
            BmffFields.Text(stream, _name);
            BmffFields.Text(stream, _location);
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 4L + ((Flags & 1) == 0 ? BmffFields.TextSize(_name, Offset) + BmffFields.TextSize(_location, Offset) : 0) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 1, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);

    private sealed class DebugView
    {
        private readonly DataEntryUrnBox _target;

        public DebugView(DataEntryUrnBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
