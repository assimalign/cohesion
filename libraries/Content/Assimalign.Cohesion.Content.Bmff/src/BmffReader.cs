using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A forward-only, header-first cursor over borrowed BMFF input.</summary>
/// <remarks>Read advances without materializing. Current materializes on demand. Payload is an
/// exclusive bounded stream. Advancing drains unread forward-only bytes through a fixed buffer.
/// Disposal never closes the caller's source.</remarks>
public abstract class BmffReader : IDisposable, IContentReader<BmffDocument>
{
    /// <summary>Gets the current box, materializing it on first access.</summary>
    /// <exception cref="ContentFormatException">The payload is invalid, unsupported, or requires a seekable source.</exception>
    public abstract BmffBox Current { get; }
    /// <summary>Gets the current validated header.</summary>
    public abstract BmffBoxHeader Header { get; }
    /// <summary>Gets the exclusive payload stream, valid until this reader advances or is disposed.</summary>
    public abstract Stream Payload { get; }
    /// <summary>Skips unread bytes and reads the next header.</summary>
    /// <returns>Whether another box is available.</returns>
    /// <exception cref="ContentFormatException">The header or skipped payload is malformed.</exception>
    public abstract bool Read();
    /// <summary>Opens a child cursor after validating the container prefix.</summary>
    /// <returns>A caller-owned child reader; finish it before advancing the parent.</returns>
    /// <exception cref="ContentFormatException">The container is unsupported or exceeds the depth limit.</exception>
    public abstract BmffReader ReadChildren();
    /// <summary>Skips the current payload without materializing it.</summary>
    /// <exception cref="ContentFormatException">The payload is truncated.</exception>
    public abstract void Skip();
    /// <inheritdoc/>
    public abstract void Dispose();

    /// <summary>Creates a reader borrowing the source at its current position.</summary>
    /// <param name="stream">A readable stream.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="ArgumentException">The source is unreadable.</exception>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    public static BmffReader Create(Stream stream) => Create(stream, null, null);

    /// <summary>Creates a reader with an immutable registry and explicit limits.</summary>
    /// <param name="stream">The borrowed readable source.</param>
    /// <param name="options">Limits and policy, or defaults.</param>
    /// <param name="registry">Factories, or the default registry.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="ArgumentException">The stream or limits are invalid.</exception>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    public static BmffReader Create(Stream stream, BmffReaderOptions? options, BmffBoxRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException(ErrorMessages.SourceNotReadable, nameof(stream));
        }
        options ??= new BmffReaderOptions();
        options.Validate();
        var offset = stream.CanSeek ? stream.Position : 0;
        var window = new BmffStream(stream, offset, stream.CanSeek ? stream.Length - offset : long.MaxValue - offset)
        {
            Options = options,
            Registry = registry ?? BmffBoxRegistry.Default,
            HasKnownLength = stream.CanSeek
        };
        return new BmffReaderDefault(window, true);
    }

    internal virtual BmffReaderOptions DocumentOptions => new();
    internal virtual BmffBoxRegistry DocumentRegistry => BmffBoxRegistry.Default;

    BmffDocument IContentReader<BmffDocument>.Read(Stream stream) => BmffDocument.Load(stream, DocumentOptions, DocumentRegistry);
    ValueTask<BmffDocument> IContentReader<BmffDocument>.ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        BmffDocument.LoadAsync(stream, DocumentOptions, DocumentRegistry, cancellationToken: cancellationToken);
}
