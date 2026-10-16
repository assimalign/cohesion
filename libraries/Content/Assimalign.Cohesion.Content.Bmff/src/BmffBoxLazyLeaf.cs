using System;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A leaf whose borrowed payload is captured and copied without buffering.</summary>
/// <remarks>Keep the seekable source open and unchanged through use and save. Subclasses own
/// typed interpretation and may override payload I/O for a small prefix and a lazy remainder.</remarks>
// Deviates from the repo interface-first rule per the Stage 5 BMFF composite-design requirement.
public abstract class BmffBoxLazyLeaf : BmffBox
{
    private int _bufferLimit = 16 * 1024 * 1024;

    /// <summary>Gets or sets the borrowed payload, excluding the box header.</summary>
    public BinarySegment Payload { get; set; }
    /// <inheritdoc/>
    public sealed override bool IsLeaf => true;
    /// <inheritdoc/>
    public sealed override bool IsComposite => false;
    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _bufferLimit = stream.Options.MaxBufferedPayloadSize;
        Payload = stream.GetSegment(stream.Position, stream.Remaining);
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream) => Payload.CopyTo(stream);
    /// <inheritdoc/>
    public override long GetPayloadSize() => Payload.Length;

    /// <summary>Materializes the payload only after checking the originating reader's buffer cap.</summary>
    /// <returns>A new array containing the payload.</returns>
    /// <exception cref="ContentFormatException">The payload exceeds the cap or is truncated.</exception>
    protected byte[] ReadPayloadBytes()
    {
        if (Payload.Length > _bufferLimit || Payload.Length > Array.MaxLength)
        {
            throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, Offset);
        }
        var bytes = new byte[(int)Payload.Length];
        using var input = Payload.OpenRead();
        input.ReadContentExactly(bytes);
        return bytes;
    }
}
