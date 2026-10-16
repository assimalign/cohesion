using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Represents character-based text content: an <see cref="IContent"/> whose bytes decode to text
/// through a known <see cref="Encoding"/>.
/// </summary>
/// <remarks>
/// Readers returned by <see cref="OpenText"/> and <see cref="OpenTextAsync"/> follow the same
/// ownership rules as content streams: they are owned by the caller, remain valid only while the
/// content is undisposed, and start at the beginning of the text (after any byte order mark).
/// </remarks>
public interface ITextContent : IContent
{
    /// <summary>Gets the encoding used to decode the content's bytes.</summary>
    Encoding Encoding { get; }

    /// <summary>
    /// Opens a reader that decodes the content from the beginning, skipping any byte order mark.
    /// </summary>
    /// <returns>A reader owned by the caller.</returns>
    /// <exception cref="ContentException">Thrown when single-use content is read a second time.</exception>
    /// <exception cref="System.ObjectDisposedException">Thrown when the content has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">The underlying content has no readable representation.</exception>
    /// <exception cref="System.IO.IOException">Opening or reading the underlying content fails.</exception>
    /// <remarks>The returned reader can report <see cref="ContentFormatException"/> with InvalidValue when a strict decoder encounters malformed bytes; decoding is deferred until the reader consumes them.</remarks>
    TextReader OpenText();

    /// <summary>
    /// Opens a reader that decodes the content from the beginning, skipping any byte order mark.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A reader owned by the caller.</returns>
    /// <exception cref="ContentException">Thrown when single-use content is read a second time.</exception>
    /// <exception cref="System.ObjectDisposedException">Thrown when the content has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">The underlying content has no readable representation.</exception>
    /// <exception cref="System.IO.IOException">Opening or reading the underlying content fails.</exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>The returned reader can report <see cref="ContentFormatException"/> with InvalidValue when a strict decoder encounters malformed bytes; decoding is deferred until the reader consumes them.</remarks>
    ValueTask<TextReader> OpenTextAsync(CancellationToken cancellationToken = default);
}
