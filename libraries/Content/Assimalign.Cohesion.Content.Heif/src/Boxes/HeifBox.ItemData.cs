using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The idat leaf with an opaque, borrowed payload; no codec parsing.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ItemDataBox : BmffBoxLazyLeaf
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
}
