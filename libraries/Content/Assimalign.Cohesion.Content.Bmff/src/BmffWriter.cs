using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Writes box trees and streaming containers without seeking the output.</summary>
/// <remarks>Supplied streams are borrowed. Sizes are computed before headers. Unknown-size containers
/// buffer at most the configured cap; supplied-size containers stream directly.</remarks>
public abstract class BmffWriter : IDisposable, IContentWriter<BmffDocument>
{
    /// <summary>Writes a complete box tree with precomputed sizes.</summary>
    /// <param name="box">The root box.</param>
    /// <exception cref="ContentFormatException">The tree is invalid, too deep or unsupported.</exception>
    public abstract void Write(BmffBox box);
    /// <summary>Writes a box tree with asynchronous output and lazy-payload copy-through.</summary>
    /// <param name="box">The root box.</param>
    /// <param name="cancellationToken">Cancellation for output operations.</param>
    /// <returns>A task completing after the box.</returns>
    /// <exception cref="ContentFormatException">The model is invalid or exceeds limits.</exception>
    public abstract ValueTask WriteAsync(BmffBox box, CancellationToken cancellationToken = default);
    /// <summary>Starts a streaming container, with a known payload size or bounded buffering.</summary>
    /// <param name="type">The raw box type.</param>
    /// <param name="payloadSize">The byte count excluding the header; null selects buffering.</param>
    /// <param name="extendedType">The UUID for a uuid box.</param>
    /// <exception cref="ContentFormatException">The depth limit is exceeded.</exception>
    public abstract void BeginContainer(BmffBoxType type, long? payloadSize = null, Guid? extendedType = null);
    /// <summary>Finishes a streaming container and verifies its supplied size.</summary>
    /// <exception cref="ContentFormatException">The emitted size differs from the supplied size.</exception>
    /// <exception cref="InvalidOperationException">There is no open container.</exception>
    public abstract void EndContainer();
    /// <summary>Copies a raw payload segment inside the current streaming container.</summary>
    /// <param name="payload">The bytes to copy without materialization.</param>
    /// <exception cref="InvalidOperationException">There is no open container.</exception>
    public abstract void WritePayload(BinarySegment payload);
    /// <inheritdoc/>
    public abstract void Dispose();

    /// <summary>Creates a writer borrowing its output.</summary>
    /// <param name="stream">The writable output.</param>
    /// <param name="maxBufferedPayloadSize">The buffering cap; defaults to 16 MiB.</param>
    /// <param name="maxDepth">The nesting cap; defaults to 64.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="ArgumentException">The output or limits are invalid.</exception>
    /// <exception cref="ArgumentNullException">The output is null.</exception>
    public static BmffWriter Create(Stream stream, int maxBufferedPayloadSize = 16 * 1024 * 1024, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException(ErrorMessages.OutputNotWritable, nameof(stream));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(maxBufferedPayloadSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBufferedPayloadSize, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        return new BmffWriterDefault(stream, maxBufferedPayloadSize, maxDepth);
    }

    internal virtual int DocumentBufferLimit => 16 * 1024 * 1024;
    internal virtual int DocumentMaxDepth => 64;

    void IContentWriter<BmffDocument>.Write(Stream stream, BmffDocument document) =>
        document.SaveCore(stream, DocumentBufferLimit, DocumentMaxDepth);
    ValueTask IContentWriter<BmffDocument>.WriteAsync(Stream stream, BmffDocument document, CancellationToken cancellationToken) =>
        document.SaveCoreAsync(stream, DocumentBufferLimit, DocumentMaxDepth, cancellationToken);
}
