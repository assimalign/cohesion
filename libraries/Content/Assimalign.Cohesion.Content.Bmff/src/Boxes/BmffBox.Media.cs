using System;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The Media container and its ordered child boxes.</summary>
public sealed class MediaBox : BmffBoxComposite
{
    internal override bool SupportsAsyncPlan => true;
    /// <summary>Creates a Media box with its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    public MediaBox(long offset)
    {
        SetOriginalLocation(offset);
    }

    /// <summary>Creates a Media box with its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public MediaBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.Media;
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => base.Read(stream);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => base.Write(stream);
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
