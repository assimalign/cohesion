using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The idat leaf with an opaque, borrowed payload; no codec parsing.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ItemDataBox : BmffBoxComposite
{
    /// <summary>Creates an empty ItemDataBox model.</summary>
    public ItemDataBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x69646174;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    // BMFF buffers external leaves. Composite dispatch is the local streaming adapter;
    // these nodes remain semantic leaves and cannot acquire children (see DESIGN.md).
    /// <inheritdoc/>
    public override IList<BmffBox> Children { get; } = Array.AsReadOnly(Array.Empty<BmffBox>());
    /// <summary>Gets or sets the lazy payload.</summary>
    public BinarySegment Payload { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream) => Payload = HeifFields.Segment(stream);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => Payload.CopyTo(stream);
    /// <inheritdoc/>
    public override long GetPayloadSize() => Payload.Length;
}
