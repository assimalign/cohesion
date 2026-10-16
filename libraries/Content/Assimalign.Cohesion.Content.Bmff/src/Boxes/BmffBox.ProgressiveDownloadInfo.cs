using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The ProgressiveDownloadInfo box; ISO/IEC 14496-12 8.1.3.</summary>
public sealed class ProgressiveDownloadInfoBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a ProgressiveDownloadInfoBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public ProgressiveDownloadInfoBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.ProgressiveDownloadInfo;

    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the download rate in bytes per second.</summary>
        public uint Rate { get; set; }
        /// <summary>Gets or sets the initial delay in milliseconds.</summary>
        public uint InitialDelay { get; set; }
    }
    /// <summary>Gets or sets the download hints.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        var count = BmffFields.Count(stream, (ulong)(stream.Remaining / 8), 8, 8);
        Entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            Entries[i] = new Entry { Rate = BmffFields.U32(stream), InitialDelay = BmffFields.U32(stream) };
        }

        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        foreach (var value in Entries) { BmffFields.U32(stream, value.Rate); BmffFields.U32(stream, value.InitialDelay); }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 4L + 8L * Entries.Length + _trailing.Length;
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
}
