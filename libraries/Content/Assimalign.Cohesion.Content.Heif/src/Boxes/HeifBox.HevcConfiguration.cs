using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The hvcC leaf with an opaque, borrowed payload; no codec parsing.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class HevcConfigurationBox : BmffBoxLazyLeaf
{
    /// <summary>Creates an empty HevcConfigurationBox model.</summary>
    public HevcConfigurationBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x68766343;
    internal HeifReaderOptions Options { get; set; } = new();
}
