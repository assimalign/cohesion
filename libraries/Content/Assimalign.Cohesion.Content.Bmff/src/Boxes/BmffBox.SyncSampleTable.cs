using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SyncSampleTable box; ISO/IEC 14496-12 8.6.2.</summary>
public sealed class SyncSampleTableBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SyncSampleTableBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SyncSampleTableBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SyncSampleTable;

    /// <summary>Gets or sets the one-based sync sample numbers.</summary>
    public uint[] Entries { get; set; } = Array.Empty<uint>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);

        var count = BmffFields.Count(stream, BmffFields.U32(stream), 4, 4);
        Entries = new uint[count];
        for (var index = 0; index < count; index++)
        {
            Entries[index] = BmffFields.U32(stream);
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
            BmffFields.U32(stream, value);
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 8L + (4) * (long)Entries.Length + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        uint previous = 0;
        foreach (var value in Entries)
        {
            if (value <= previous)
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }

            previous = value;
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
}
