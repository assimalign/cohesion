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
    void Write(Stream stream, TDocument document);

    /// <summary>
    /// Writes a document to the stream.
    /// </summary>
    /// <param name="stream">The writable stream to emit into.</param>
    /// <param name="document">The document to write.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task representing the write.</returns>
    ValueTask WriteAsync(Stream stream, TDocument document, CancellationToken cancellationToken = default);
}
