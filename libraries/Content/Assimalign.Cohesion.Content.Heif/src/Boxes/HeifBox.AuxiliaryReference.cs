using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The auxl item reference; its ID width comes from the enclosing iref version.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class AuxiliaryReferenceBox : BmffBox
{
    /// <summary>Creates an empty AuxiliaryReferenceBox model.</summary>
    public AuxiliaryReferenceBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x6175786c;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;

    private readonly HeifReferenceFields _fields = new();
    /// <summary>Gets or sets whether IDs have 32 bits; must match the enclosing iref version.</summary>
    public bool LargeIds { get => _fields.LargeIds; set => _fields.LargeIds = value; }
    /// <summary>Gets or sets the referring item ID.</summary>
    public uint FromItemId { get { _fields.Decode(Options); return _fields.FromItemId; } set { _fields.Decode(Options); _fields.FromItemId = value; } }
    /// <summary>Gets or sets the referenced item IDs in encoded order.</summary>
    public uint[] ToItemIds { get { _fields.Decode(Options); return _fields.ToItemIds; } set { _fields.Decode(Options); _fields.ToItemIds = value; } }
    /// <summary>Gets or sets bytes following the defined reference list.</summary>
    public byte[] TrailingData { get { _fields.Decode(Options); return _fields.TrailingData; } set { _fields.Decode(Options); _fields.TrailingData = value; } }
    internal HeifReferenceFields Fields => _fields;
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => _fields.Read(stream, Options);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => _fields.Write(stream, Options);
    /// <inheritdoc/>
    public override long GetPayloadSize() => _fields.Size(Options);
}
