using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>The opaque av1C payload; configuration bytes remain borrowed and are never codec-decoded.</summary>
/// <remarks>Full-box prefixes, where present, are included in Data.</remarks>
// Deviates from the repo interface-first rule per the Stage 4 brief's sealed box model.
public sealed class Av1ConfigurationBox : BmffBoxLazyLeaf, IMp4OpaqueBox
{
    /// <summary>Creates an empty configuration box.</summary>
    public Av1ConfigurationBox() { }
    internal Av1ConfigurationBox(BmffBoxHeader header) { Offset = header.Offset; Limit = header.TotalSize ?? 0; }
    /// <summary>Gets or sets the complete opaque payload, excluding the box header.</summary>
    public BinarySegment Data { get => Payload; set => Payload = value; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x61763143;
}
