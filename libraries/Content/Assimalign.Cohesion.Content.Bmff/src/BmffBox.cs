using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A node in the BMFF composite tree, with payload I/O and visitor dispatch.</summary>
// Deviates from the repo interface-first rule per the owner's Stage 2 composite-design requirement.
public abstract class BmffBox
{
    private long _initialOffset;
    private long _initialLimit;

    /// <summary>Gets whether this node is a leaf rather than a composite, even when a composite is empty.</summary>
    public virtual bool IsLeaf => this is not BmffBoxComposite;
    /// <summary>Gets whether this node derives from <see cref="BmffBoxComposite"/>.</summary>
    public virtual bool IsComposite => this is BmffBoxComposite;
    /// <summary>Gets the original total size from the header, or the constructor-supplied size before loading.</summary>
    /// <remarks>This is a byte count, not an end offset. It does not track edits. New boxes retain
    /// their constructor value (zero if omitted); writing computes a separate size without changing it.</remarks>
    public virtual long Limit => Header.HeaderSize == 0 ? _initialLimit : Header.TotalSize ?? 0;
    /// <summary>Gets the absolute source position at which this box's header was read.</summary>
    /// <remarks>This position does not track edits, reordering, relocation or saving. Before loading,
    /// it is the constructor-supplied position, or zero if omitted. The writer's output layout is separate.</remarks>
    public virtual long Offset => Header.HeaderSize == 0 ? _initialOffset : Header.Offset;

    internal void SetOriginalLocation(long offset, long limit = 0)
    {
        _initialOffset = offset;
        _initialLimit = limit;
    }

    /// <summary>Gets the raw FourCC, including unregistered types.</summary>
    public abstract BmffBoxType BoxType { get; }
    /// <summary>Gets the original header, or default for a newly constructed model.</summary>
    public BmffBoxHeader Header { get; internal set; }

    /// <summary>Appends already materialized fields for tree inspection; the default writes nothing.</summary>
    /// <param name="writer">The borrowed output; prefix fields with a space and do not write a newline.</param>
    /// <remarks>Overrides must not read payloads, compute write plans or mutate the model. Call the base
    /// implementation to retain inherited details, including full-box version and flags. The formatter
    /// escapes control characters and supplies an invariant-culture writer.</remarks>
    /// <exception cref="ArgumentNullException">The writer is null.</exception>
    /// <exception cref="IOException">Writing fails.</exception>
    /// <exception cref="ObjectDisposedException">The output is disposed.</exception>
    protected internal virtual void WriteDescription(TextWriter writer) => ArgumentNullException.ThrowIfNull(writer);

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
