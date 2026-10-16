using System;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A node in the BMFF composite tree, with payload I/O and visitor dispatch.</summary>
// Deviates from the repo interface-first rule per the owner's Stage 2 composite-design requirement.
public abstract class BmffBox
{
    /// <summary>Gets whether the box has no child boxes.</summary>
    public abstract bool IsLeaf { get; }
    /// <summary>Gets whether the box contains child boxes.</summary>
    public abstract bool IsComposite { get; }
    /// <summary>Gets the original total size supplied at creation, not the recomputed serialized size.</summary>
    public abstract long Limit { get; }
    /// <summary>Gets the original absolute header offset.</summary>
    public abstract long Offset { get; }
    /// <summary>Gets the raw FourCC, including unregistered types.</summary>
    public abstract BmffBoxType BoxType { get; }
    /// <summary>Gets the original header, or default for a newly constructed model.</summary>
    public BmffBoxHeader Header { get; internal set; }
    /// <summary>Reads the payload from its exclusive bounded window.</summary>
    /// <param name="stream">The payload window.</param>
    /// <exception cref="ContentFormatException">The payload is invalid or unsupported.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.OperationCanceledException">The payload window's cancellation token was canceled.</exception>
    public abstract void Read(BmffStream stream);
    /// <summary>Writes only the payload; BmffWriter writes the enclosing header.</summary>
    /// <param name="stream">The bounded payload output.</param>
    /// <exception cref="ContentFormatException">The model is invalid or unsupported.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.OperationCanceledException">The payload window's cancellation token was canceled.</exception>
    public abstract void Write(BmffStream stream);
    /// <summary>Computes the payload byte count before writing its header.</summary>
    /// <returns>The nonnegative payload size.</returns>
    /// <exception cref="ContentFormatException">The model has no implemented writer.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public virtual long GetPayloadSize() =>
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
    /// <summary>Dispatches to the type-specific visitor.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="visitor">The visitor.</param>
    /// <returns>The visitor result.</returns>
    /// <exception cref="System.NullReferenceException">The visitor is null.</exception>
    public virtual T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <summary>Dispatches to the type-specific visitor.</summary>
    /// <param name="visitor">The visitor.</param>
    /// <exception cref="System.NullReferenceException">The visitor is null.</exception>
    public virtual void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
