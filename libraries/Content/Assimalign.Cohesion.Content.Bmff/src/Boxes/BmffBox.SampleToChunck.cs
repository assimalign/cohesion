using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SampleToChunk box; ISO/IEC 14496-12 8.7.4.</summary>
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SampleToChunkBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SampleToChunkBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SampleToChunkBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SampleToChunk;

    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the one-based first chunk.</summary>
        public uint FirstChunk { get; set; }
        /// <summary>Gets or sets the number of samples per chunk.</summary>
        public uint SamplesPerChunk { get; set; }
        /// <summary>Gets or sets the one-based sample description index.</summary>
        public uint SampleDescriptionIndex { get; set; }
    }
    /// <summary>Gets or sets the ordered entries.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);

        var count = BmffFields.Count(stream, BmffFields.U32(stream), 12, 12);
        Entries = new Entry[count];
        for (var index = 0; index < count; index++)
        {
            Entries[index] = new Entry
            {
                FirstChunk = BmffFields.U32(stream),
                SamplesPerChunk = BmffFields.U32(stream),
                SampleDescriptionIndex = BmffFields.U32(stream)
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
            BmffFields.U32(stream, value.FirstChunk);
            BmffFields.U32(stream, value.SamplesPerChunk);
            BmffFields.U32(stream, value.SampleDescriptionIndex);
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 8L + (12) * (long)Entries.Length + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        uint previous = 0;
        foreach (var value in Entries)
        {
            if (value.FirstChunk <= previous || (previous == 0 && value.FirstChunk != 1) || value.SamplesPerChunk == 0 || value.SampleDescriptionIndex == 0)
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }

            previous = value.FirstChunk;
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
        private readonly SampleToChunkBox _target;

        public DebugView(SampleToChunkBox value)
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
