using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SampleSize box; ISO/IEC 14496-12 8.7.3.2.</summary>
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SampleSizeBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SampleSizeBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SampleSizeBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SampleSize;

    /// <summary>Gets or sets the common sample size, or zero for individual sizes.</summary>
    public uint SampleSize { get; set; }
    /// <summary>Gets or sets the count when SampleSize is nonzero; otherwise the entries length is written.</summary>
    public uint SampleCount { get; set; }
    /// <summary>Gets or sets the individual sizes when SampleSize is zero.</summary>
    public uint[] Entries { get; set; } = Array.Empty<uint>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        SampleSize = BmffFields.U32(stream);
        SampleCount = BmffFields.U32(stream);
        var count = BmffFields.Count(stream, SampleCount, SampleSize == 0 ? 4 : 0, SampleSize == 0 ? 4 : 0);
        Entries = SampleSize == 0 ? new uint[count] : Array.Empty<uint>();
        for (var i = 0; i < Entries.Length; i++)
        {
            Entries[i] = BmffFields.U32(stream);
        }

        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, SampleSize);
        BmffFields.U32(stream, SampleSize == 0 ? (uint)Entries.Length : SampleCount);
        if (SampleSize == 0)
        {
            foreach (var value in Entries)
            {
                BmffFields.U32(stream, value);
            }
        }

        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 12L + (SampleSize == 0 ? Entries.LongLength * 4 : 0) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" samples={(SampleSize == 0 ? (long)Entries.Length : SampleCount)} sample-size={SampleSize}"));
    }

    private sealed class DebugView
    {
        private readonly SampleSizeBox _target;

        public DebugView(SampleSizeBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public uint SampleSize => _target.SampleSize;
        public uint SampleCount => _target.SampleCount;
        public uint[] Entries => _target.Entries;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
