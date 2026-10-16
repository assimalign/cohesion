using System;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SchemeInformation container and its ordered child boxes.</summary>
public sealed class SchemeInformationBox : BmffBoxComposite
{
    internal override bool SupportsAsyncPlan => true;
    /// <summary>Creates a SchemeInformation box with its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SchemeInformationBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SchemeInformation;
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => base.Read(stream);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => base.Write(stream);
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
