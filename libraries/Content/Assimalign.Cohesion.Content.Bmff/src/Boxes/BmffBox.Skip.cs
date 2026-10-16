

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The Skip box with a borrowed, potentially lazy payload.</summary>
public sealed class SkipBox : BmffBox
{
    /// <summary>Creates a box with its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SkipBox(long offset, long limit)
    {
        Offset = offset;
        Limit = limit;
    }
    /// <summary>Gets or sets the payload without forcing materialization.</summary>
    public BinarySegment Payload { get; set; }
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.Skip;
    /// <inheritdoc/>
    public override void Read(BmffStream stream) => Payload = stream.GetSegment(0, stream.Length);
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => Payload.CopyTo(stream);
    /// <inheritdoc/>
    public override long GetPayloadSize() => Payload.Length;
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
