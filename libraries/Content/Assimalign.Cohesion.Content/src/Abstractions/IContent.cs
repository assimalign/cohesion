using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Represents a piece of content: named, format-identified data that can be read as a stream.
/// </summary>
/// <remarks>
/// <para>
/// A content instance is the format-neutral unit the Content library family works in: format packages
/// parse content into typed documents, services serve content over transports, and composites group
/// content into containers. The contract deliberately carries no storage concern — content may be
/// backed by memory, a stream, a file, or a remote source.
/// </para>
/// <para>
/// <b>Stream ownership.</b> Streams returned by <see cref="OpenRead"/> and <see cref="OpenReadAsync"/>
/// are owned by the caller and must be disposed by the caller. They remain valid only while the content
/// itself is undisposed. Whether the content owns or borrows its own backing resource is decided when
/// the content is created (see <c>ContentFactory</c>); disposing the content disposes owned backing
/// resources and leaves borrowed ones open.
/// </para>
/// <para>
/// <b>Reopenability.</b> When <see cref="CanReopen"/> is <see langword="true"/> the content can be read
/// any number of times, each read starting from the beginning. Single-use content (for example content
/// over a non-seekable network stream) reports <see langword="false"/> and permits exactly one read.
/// </para>
/// </remarks>
public interface IContent : IDisposable, IAsyncDisposable
{
    /// <summary>Gets the optional name of the content, such as a file name, entry name, or track name.</summary>
    string? Name { get; }

    /// <summary>Gets the format descriptor identifying what kind of content this is.</summary>
    ContentFormat Format { get; }

    /// <summary>Gets the concrete media type of this instance, when known. Falls back to the format's media types when <see langword="null"/>.</summary>
    string? MediaType { get; }

    /// <summary>Gets the length of the content in bytes, or <see langword="null"/> when the length is not known up front.</summary>
    long? Length { get; }

    /// <summary>Gets a value indicating whether the content is read-only. Writable content additionally implements <see cref="IWritableContent"/>.</summary>
    bool IsReadOnly { get; }

    /// <summary>Gets a value indicating whether the content can be read more than once. See the remarks on <see cref="IContent"/>.</summary>
    bool CanReopen { get; }

    /// <summary>
    /// Opens a read stream over the content, positioned at the beginning.
    /// </summary>
    /// <returns>A readable stream owned by the caller. See the remarks on <see cref="IContent"/> for the ownership contract.</returns>
    /// <exception cref="ContentException">Thrown when single-use content is read a second time.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the content has been disposed.</exception>
    /// <exception cref="NotSupportedException">Thrown when the content has no serialized representation of its own, such as a pure composite.</exception>
    Stream OpenRead();

    /// <summary>
    /// Opens a read stream over the content, positioned at the beginning.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A readable stream owned by the caller. See the remarks on <see cref="IContent"/> for the ownership contract.</returns>
    /// <exception cref="ContentException">Thrown when single-use content is read a second time.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the content has been disposed.</exception>
    /// <exception cref="NotSupportedException">Thrown when the content has no serialized representation of its own, such as a pure composite.</exception>
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}
