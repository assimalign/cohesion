using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>The opaque avcC payload; configuration bytes remain borrowed and are never codec-decoded.</summary>
/// <remarks>Composite inheritance is a local workaround for BMFF's buffered external-leaf path.
/// This is a leaf: its child collection is permanently empty. Full-box prefixes, where present, are included in Data.</remarks>
// Deviates from the repo interface-first rule per the Stage 4 brief's sealed box model.
public sealed class AvcConfigurationBox : BmffBoxComposite, IMp4OpaqueBox
{
    /// <summary>Creates an empty configuration box.</summary>
    public AvcConfigurationBox() { }
    internal AvcConfigurationBox(BmffBoxHeader header) { Offset = header.Offset; Limit = header.TotalSize ?? 0; }
    /// <summary>Gets or sets the complete opaque payload, excluding the box header.</summary>
    public BinarySegment Data { get; set; }
    /// <summary>Gets the permanently empty child list; this opaque box has no children.</summary>
    public override IList<BmffBox> Children => Array.Empty<BmffBox>();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x61766343;
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => Data = Mp4OpaquePayload.Read(stream);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => Data.CopyTo(stream);
    /// <inheritdoc/>
    public override long GetPayloadSize() => Data.Length;
}
