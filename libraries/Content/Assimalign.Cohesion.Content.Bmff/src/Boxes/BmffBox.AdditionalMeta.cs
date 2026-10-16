using System;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The AdditionalMeta container and its ordered child boxes.</summary>
public sealed class AdditionalMetaBox : BmffBoxComposite
{
    internal override bool SupportsAsyncPlan => true;
    /// <summary>Creates a AdditionalMeta box with its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public AdditionalMetaBox(long offset, long limit)
    {
        Offset = offset;
        Limit = limit;
    }

    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.AdditionalMeta;
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => base.Read(stream);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => base.Write(stream);
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override string ToString() => "Bmff Box: Additional Metadata (meco)";
}
