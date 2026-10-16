using System;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>An unregistered or unsupported box preserved without interpreting its payload.</summary>
public sealed class UnknownBox : BmffBox
{
    private UnknownBox(BmffBoxHeader header, BinarySegment payload, ContentFormatErrorCode reason)
    {
        Header = header;
        Payload = payload;
        PreservationReason = reason;
    }

    /// <summary>Creates a preservation node from a validated header and borrowed segment.</summary>
    /// <param name="header">The original header.</param>
    /// <param name="payload">The payload, excluding the header.</param>
    /// <param name="reason">The reason for preserving instead of parsing.</param>
    /// <returns>The new box.</returns>
    /// <exception cref="ArgumentException">The header is uninitialized or its size differs from the segment.</exception>
    public static UnknownBox Create(BmffBoxHeader header, BinarySegment payload, ContentFormatErrorCode reason = ContentFormatErrorCode.Unsupported)
    {
        if (header.HeaderSize == 0 || header.TotalSize - header.HeaderSize != payload.Length)
        {
            throw new ArgumentException(ErrorMessages.InvalidBoxSize, nameof(header));
        }
        return new UnknownBox(header, payload, reason);
    }

    /// <summary>Gets or sets the payload; lazy segments borrow their source.</summary>
    public BinarySegment Payload { get; set; }
    /// <summary>Gets why the reader preserved this box.</summary>
    public ContentFormatErrorCode PreservationReason { get; }
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize!.Value;
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => Header.BoxType;
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
