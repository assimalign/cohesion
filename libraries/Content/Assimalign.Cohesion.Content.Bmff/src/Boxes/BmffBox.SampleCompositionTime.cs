using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SampleCompositionTime box; ISO/IEC 14496-12 8.6.1.3.</summary>
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SampleCompositionTimeBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SampleCompositionTimeBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SampleCompositionTimeBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SampleCompositionTime;

    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the number of consecutive samples.</summary>
        public uint SampleCount { get; set; }
        /// <summary>Gets or sets the unsigned v0 or signed v1 composition offset.</summary>
        public long SampleOffset { get; set; }
    }
    /// <summary>Gets or sets the ordered entries.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);

        var count = BmffFields.Count(stream, BmffFields.U32(stream), 8, 16);
        Entries = new Entry[count];
        for (var index = 0; index < count; index++)
        {
            Entries[index] = new Entry
            {
                SampleCount = BmffFields.U32(stream),
                SampleOffset = Version == 0 ? (long)BmffFields.U32(stream) : BmffFields.I32(stream)
            };
        }
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);

        BmffFields.U32(stream, (uint)Entries.Length);
        foreach (var value in Entries)
        {
            BmffFields.U32(stream, value.SampleCount);
            BmffFields.U32(stream, unchecked((uint)value.SampleOffset));
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 8L + (8) * (long)Entries.Length + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        foreach (var value in Entries)
        {
            if (value.SampleOffset < (Version == 0 ? 0L : int.MinValue) || value.SampleOffset > (Version == 0 ? (long)uint.MaxValue : int.MaxValue))
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" entries={Entries.Length}"));
    }

    private sealed class DebugView
    {
        private readonly SampleCompositionTimeBox _target;

        public DebugView(SampleCompositionTimeBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public Entry[] Entries => _target.Entries;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
