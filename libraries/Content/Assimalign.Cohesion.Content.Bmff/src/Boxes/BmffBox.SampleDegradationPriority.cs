using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;



namespace Assimalign.Cohesion.Content.Bmff;

[DebuggerDisplay("Bmff Box: Sample Degradation Priority (stdp)")]
/// <summary>The SampleDegradationPriorityBox model; field-level support is documented in DESIGN.md.</summary>
public sealed class SampleDegradationPriorityBox : BmffFullBox
{
    /// <summary>Creates a SampleDegradationPriorityBox at its original location.</summary>
    /// <param name="offset">The absolute original header offset.</param>
    /// <param name="limit">The original total byte size.</param>
    public SampleDegradationPriorityBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    /// <inheritdoc/>
    public override bool IsLeaf => true;

    /// <inheritdoc/>
    public override bool IsComposite => false;

    /// <inheritdoc/>
    public override long Limit { get;}

    /// <inheritdoc/>
    public override long Offset { get; }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SampleDegradationPriority;

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
    }

    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
