using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The Meta container and its ordered child boxes.</summary>
public sealed class MetaBox : BmffBoxComposite
{
    internal override bool SupportsAsyncPlan => true;
    /// <summary>Creates a Meta box with its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public MetaBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.Meta;
    private uint _flags;
    /// <summary>Gets or sets the full-box version. Version zero is implemented.</summary>
    public byte Version { get; set; }
    /// <summary>Gets or sets the 24-bit flags. Zero flags are implemented.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value exceeds 24 bits.</exception>
    public uint Flags
    {
        get => _flags;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 0xFFFFFFu);
            _flags = value;
        }
    }
    internal override int PrefixSize => 4;
    internal override int? ReadPrefix(BmffStream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        var prefix = BmffBinary.ReadField(stream, bytes).ReadUInt32BigEndian();
        Version = (byte)(prefix >> 24);
        Flags = prefix & 0xFFFFFF;
        ValidateVersion();
        return null;
    }
    internal override void WritePrefix(BmffStream stream)
    {
        ValidateVersion();
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ((uint)Version << 24) | Flags);
        stream.Write(bytes);
    }
    private void ValidateVersion()
    {
        if (Version != 0 || Flags != 0)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVersion, ContentFormatErrorCode.Unsupported, Offset);
        }
    }
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => base.Read(stream);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => base.Write(stream);
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" version={Version} flags=0x{Flags:X6}"));
    }
}
