using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The ItemProtection container and its ordered child boxes.</summary>
public sealed class ItemProtectionBox : BmffBoxComposite
{
    internal override bool SupportsAsyncPlan => true;
    /// <summary>Creates a ItemProtection box with its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public ItemProtectionBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.ItemProtection;
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
    internal override int PrefixSize => 6;
    internal override int? ReadPrefix(BmffStream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        var prefix = BmffBinary.ReadField(stream, bytes).ReadUInt32BigEndian();
        Version = (byte)(prefix >> 24);
        Flags = prefix & 0xFFFFFF;
        ValidateVersion();
        return BmffBinary.ReadField(stream, bytes[..2]).ReadUInt16BigEndian();
    }
    internal override void WritePrefix(BmffStream stream)
    {
        ValidateVersion();
        Span<byte> bytes = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ((uint)Version << 24) | Flags);
        if (Children.Count > ushort.MaxValue)
        {
            throw new ContentFormatException(ErrorMessages.ChildCountMismatch, ContentFormatErrorCode.LimitExceeded, Offset);
        }
        ValidateChildren(Children.Count);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], (ushort)Children.Count);
        stream.Write(bytes);
    }
    private void ValidateVersion()
    {
        if (Version != 0 || Flags != 0)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVersion, ContentFormatErrorCode.Unsupported, Offset);
        }
    }
    internal override void ValidateChildren(int? expectedCount)
    {
        base.ValidateChildren(expectedCount);
        foreach (var child in Children)
        {
            if (child.BoxType != BmffBoxType.ProtectionSchemeInfo)
            {
                throw new ContentFormatException(ErrorMessages.ChildCountMismatch, ContentFormatErrorCode.InvalidStructure, child.Offset);
            }
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
