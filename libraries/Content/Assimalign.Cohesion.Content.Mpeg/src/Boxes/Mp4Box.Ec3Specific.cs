using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>The opaque dec3 payload; configuration bytes remain borrowed and are never codec-decoded.</summary>
/// <remarks>Full-box prefixes, where present, are included in Data.</remarks>
// Deviates from the repo interface-first rule per the Stage 4 brief's sealed box model.
public sealed class Ec3SpecificBox : BmffBoxLazyLeaf, IMp4OpaqueBox
{
    /// <summary>Creates an empty configuration box.</summary>
    public Ec3SpecificBox() { }
    internal Ec3SpecificBox(BmffBoxHeader header) { }
    /// <summary>Gets or sets the complete opaque payload, excluding the box header.</summary>
    public BinarySegment Data { get => Payload; set => Payload = value; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x64656333;
}
