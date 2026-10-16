using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The ItemInfoEntry box; ISO/IEC 14496-12 8.11.6.</summary>
public sealed class ItemInfoEntryBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates an empty ItemInfoEntryBox.</summary>
    public ItemInfoEntryBox() { Version = 2; }
    internal ItemInfoEntryBox(long offset, long limit) { SetOriginalLocation(offset, limit); }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.ItemInfoEntry;

    /// <summary>Gets or sets the item identifier.</summary>
    public uint ItemId { get; set; }
    /// <summary>Gets or sets the protection entry index.</summary>
    public ushort ItemProtectionIndex { get; set; }
    /// <summary>Gets or sets the item type.</summary>
    public FourCC ItemType { get; set; }
    private byte[] _itemName = Array.Empty<byte>();
    /// <summary>Gets or sets the item name. Original UTF-8 bytes are preserved until assigned.</summary>
    public string ItemName { get => Encoding.UTF8.GetString(_itemName); set => _itemName = Encoding.UTF8.GetBytes(value); }
    private byte[] _contentType = Array.Empty<byte>();
    /// <summary>Gets or sets the MIME content type for mime items. Original UTF-8 bytes are preserved until assigned.</summary>
    public string ContentType { get => Encoding.UTF8.GetString(_contentType); set => _contentType = Encoding.UTF8.GetBytes(value); }
    private byte[] _contentEncoding = Array.Empty<byte>();
    /// <summary>Gets or sets the optional MIME content encoding. Original UTF-8 bytes are preserved until assigned.</summary>
    public string ContentEncoding { get => Encoding.UTF8.GetString(_contentEncoding); set => _contentEncoding = Encoding.UTF8.GetBytes(value); }
    /// <summary>Gets or sets whether the optional MIME content-encoding string is encoded.</summary>
    public bool HasContentEncoding { get; set; }
    private byte[] _itemUriType = Array.Empty<byte>();
    /// <summary>Gets or sets the URI type for uri items. Original UTF-8 bytes are preserved until assigned.</summary>
    public string ItemUriType { get => Encoding.UTF8.GetString(_itemUriType); set => _itemUriType = Encoding.UTF8.GetBytes(value); }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 3, 1, stream.Offset);
        if (Version < 2)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVersion, ContentFormatErrorCode.Unsupported, stream.Offset);
        }

        ItemId = Version == 2 ? BmffFields.U16(stream) : BmffFields.U32(stream);
        ItemProtectionIndex = BmffFields.U16(stream);
        ItemType = FourCC.FromUInt32(BmffFields.U32(stream));
        _itemName = BmffFields.Text(stream);
        if (ItemType.Value == 0x6d696d65)
        {
            _contentType = BmffFields.Text(stream);
            HasContentEncoding = stream.Remaining != 0;
            if (HasContentEncoding)
            {
                _contentEncoding = BmffFields.Text(stream);
            }
        }
        else if (ItemType.Value == 0x75726920)
        {
            _itemUriType = BmffFields.Text(stream);
        }

        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.Sized(stream, ItemId, Version == 2 ? 2 : 4);
        BmffFields.U16(stream, ItemProtectionIndex);
        BmffFields.U32(stream, ItemType.Value);
        BmffFields.Text(stream, _itemName);
        if (ItemType.Value == 0x6d696d65)
        {
            BmffFields.Text(stream, _contentType);
            if (HasContentEncoding)
            {
                BmffFields.Text(stream, _contentEncoding);
            }
        }
        else if (ItemType.Value == 0x75726920)
        {
            BmffFields.Text(stream, _itemUriType);
        }

        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return (Version == 2 ? 12L : 14L) + BmffFields.TextSize(_itemName, Offset) + (ItemType.Value == 0x6d696d65 ? BmffFields.TextSize(_contentType, Offset) + (HasContentEncoding ? BmffFields.TextSize(_contentEncoding, Offset) : 0) : ItemType.Value == 0x75726920 ? BmffFields.TextSize(_itemUriType, Offset) : 0) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 3, 1, Offset);
        if (Version < 2)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVersion, ContentFormatErrorCode.Unsupported, Offset);
        }

        BmffFields.Width(ItemId, Version == 2 ? 2 : 4, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
