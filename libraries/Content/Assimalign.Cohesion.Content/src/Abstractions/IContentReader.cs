using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Reads a format-specific document from a stream. Format packages implement this contract for their
/// document type, for example a YAML package reading a YAML document or a BMFF package reading a box tree.
/// </summary>
/// <typeparam name="TDocument">The format-specific document type produced by the reader.</typeparam>
/// <remarks>
/// The reader consumes from the stream's current position and does not dispose the stream. Malformed
/// input is reported through <see cref="ContentFormatException"/> (or a format package's derived
/// exception), never through raw parsing exceptions.
/// </remarks>
public interface IContentReader<TDocument>
{
    /// <summary>
    /// Reads a document from the stream.
    /// </summary>
    /// <param name="stream">The readable stream to consume.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ContentFormatException">Thrown when the input is malformed for the format.</exception>
    /// <exception cref="System.ArgumentNullException">The source stream is null.</exception>
    /// <exception cref="System.IO.IOException">Reading the source fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">The source rejects a required stream operation.</exception>
    /// <exception cref="System.ArgumentException">The source does not satisfy this reader's stream requirements.</exception>
    TDocument Read(Stream stream);

    /// <summary>
    /// Reads a document from the stream.
    /// </summary>
    /// <param name="stream">The readable stream to consume.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ContentFormatException">Thrown when the input is malformed for the format.</exception>
    /// <exception cref="System.ArgumentNullException">The source stream is null.</exception>
    /// <exception cref="System.IO.IOException">Reading the source fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The source has been disposed.</exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="System.NotSupportedException">The source rejects a required stream operation.</exception>
    /// <exception cref="System.ArgumentException">The source does not satisfy this reader's stream requirements.</exception>
    ValueTask<TDocument> ReadAsync(Stream stream, CancellationToken cancellationToken = default);
}
