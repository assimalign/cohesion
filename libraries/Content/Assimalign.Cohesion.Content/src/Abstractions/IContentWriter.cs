using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Writes a format-specific document to a stream. Format packages implement this contract for their
/// document type as the counterpart to <see cref="IContentReader{TDocument}"/>.
/// </summary>
/// <typeparam name="TDocument">The format-specific document type consumed by the writer.</typeparam>
/// <remarks>
/// The writer emits at the stream's current position and does not dispose the stream.
/// </remarks>
public interface IContentWriter<TDocument>
{
    /// <summary>
    /// Writes a document to the stream.
    /// </summary>
    /// <param name="stream">The writable stream to emit into.</param>
    /// <param name="document">The document to write.</param>
    /// <exception cref="System.ArgumentNullException">The destination or a required document is null.</exception>
    /// <exception cref="ContentFormatException">The document cannot be encoded, is unsupported, or exceeds a format limit.</exception>
    /// <exception cref="System.IO.IOException">Reading retained payloads or writing the destination fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The document, a retained source or the destination has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">A retained source or the destination rejects a required stream operation.</exception>
    /// <exception cref="System.ArgumentException">The destination is unsuitable or a document value cannot be encoded.</exception>
    /// <exception cref="System.InvalidOperationException">The document or writer state does not allow the requested save.</exception>
    void Write(Stream stream, TDocument document);

    /// <summary>
    /// Writes a document to the stream.
    /// </summary>
    /// <param name="stream">The writable stream to emit into.</param>
    /// <param name="document">The document to write.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task representing the write.</returns>
    /// <exception cref="System.ArgumentNullException">The destination or a required document is null.</exception>
    /// <exception cref="ContentFormatException">The document cannot be encoded, is unsupported, or exceeds a format limit.</exception>
    /// <exception cref="System.IO.IOException">Reading retained payloads or writing the destination fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The document, a retained source or the destination has been disposed.</exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="System.NotSupportedException">A retained source or the destination rejects a required stream operation.</exception>
    /// <exception cref="System.ArgumentException">The destination is unsuitable or a document value cannot be encoded.</exception>
    /// <exception cref="System.InvalidOperationException">The document or writer state does not allow the requested save.</exception>
    ValueTask WriteAsync(Stream stream, TDocument document, CancellationToken cancellationToken = default);
}
