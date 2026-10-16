using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The dimg item reference; its ID width comes from the enclosing iref version.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class DerivedImageReferenceBox : BmffBox
{
    /// <summary>Creates an empty DerivedImageReferenceBox model.</summary>
    public DerivedImageReferenceBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x64696d67;
    internal HeifReaderOptions Options { get; set; } = new();

    private readonly HeifReferenceFields _fields = new();
    /// <summary>Gets or sets whether IDs have 32 bits; must match the enclosing iref version.</summary>
    public bool LargeIds { get => _fields.LargeIds; set => _fields.LargeIds = value; }
    /// <summary>Gets or sets the referring item ID.</summary>
    /// <exception cref="ContentFormatException">The retained reference payload is malformed or exceeds the configured item-reference limit.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public uint FromItemId { get { _fields.Decode(Options); return _fields.FromItemId; } set { _fields.Decode(Options); _fields.FromItemId = value; } }
    /// <summary>Gets or sets the referenced item IDs in encoded order.</summary>
    /// <exception cref="ContentFormatException">The retained reference payload is malformed or exceeds the configured item-reference limit.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public uint[] ToItemIds { get { _fields.Decode(Options); return _fields.ToItemIds; } set { _fields.Decode(Options); _fields.ToItemIds = value; } }
    /// <summary>Gets or sets bytes following the defined reference list.</summary>
    /// <exception cref="ContentFormatException">The retained reference payload is malformed or exceeds the configured item-reference limit.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public byte[] TrailingData { get { _fields.Decode(Options); return _fields.TrailingData; } set { _fields.Decode(Options); _fields.TrailingData = value; } }
    internal HeifReferenceFields Fields => _fields;
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => _fields.Read(stream, Options);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => _fields.Write(stream, Options);
    /// <inheritdoc/>
    public override long GetPayloadSize() => _fields.Size(Options);
}
