using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The av1C leaf with an opaque, borrowed payload; no codec parsing.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class Av1ConfigurationBox : BmffBoxLazyLeaf
{
    /// <summary>Creates an empty Av1ConfigurationBox model.</summary>
    public Av1ConfigurationBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x61763143;
    internal HeifReaderOptions Options { get; set; } = new();
}
