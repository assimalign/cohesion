using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Represents content whose bytes can be replaced by writing to it, such as an in-memory buffer a
/// format writer emits into.
/// </summary>
/// <remarks>
/// Writing replaces the content: the bytes become visible to subsequent reads when the write stream is
/// disposed. Streams returned by <see cref="OpenWrite"/> and <see cref="OpenWriteAsync"/> are owned by
/// the caller and must be disposed to commit the written data.
/// </remarks>
public interface IWritableContent : IContent
{
    /// <summary>
    /// Opens a write stream that replaces the content's bytes when disposed.
    /// </summary>
    /// <returns>A writable stream owned by the caller.</returns>
    /// <exception cref="System.ObjectDisposedException">Thrown when the content has been disposed.</exception>
    Stream OpenWrite();

    /// <summary>
    /// Opens a write stream that replaces the content's bytes when disposed.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A writable stream owned by the caller.</returns>
    /// <exception cref="System.ObjectDisposedException">Thrown when the content has been disposed.</exception>
    ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default);
}
