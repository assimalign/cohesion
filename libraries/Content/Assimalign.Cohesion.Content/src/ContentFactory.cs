using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Creates content instances over common backing sources. The concrete implementations are internal;
/// the factory is the supported way to construct root content values.
/// </summary>
public static class ContentFactory
{
    /// <summary>
    /// Creates read-only, reopenable content over a block of bytes.
    /// </summary>
    /// <param name="data">The bytes backing the content. The content keeps a reference; the caller must not mutate the memory afterwards.</param>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <returns>Read-only in-memory content.</returns>
    public static IContent FromBytes(ReadOnlyMemory<byte> data, ContentFormat? format = null, string? name = null, string? mediaType = null) =>
        new MemoryContent(data, format ?? ContentFormat.Unknown, name, mediaType, isReadOnly: true);

    /// <summary>
    /// Creates an empty, writable in-memory buffer that format writers can emit into.
    /// </summary>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <returns>Writable in-memory content whose bytes are replaced on each committed write.</returns>
    public static IWritableContent CreateBuffer(ContentFormat? format = null, string? name = null, string? mediaType = null) =>
        new MemoryContent(ReadOnlyMemory<byte>.Empty, format ?? ContentFormat.Unknown, name, mediaType, isReadOnly: false);

    /// <summary>
    /// Creates read-only content over an existing stream.
    /// </summary>
    /// <param name="stream">The readable stream backing the content. Seekable streams produce reopenable content; non-seekable streams produce single-use content.</param>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <param name="leaveOpen">
    /// <see langword="false"/> (the default) to transfer ownership — disposing the content disposes the
    /// stream; <see langword="true"/> to borrow the stream and leave it open when the content is disposed.
    /// </param>
    /// <returns>Stream-backed content.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not readable.</exception>
    public static IContent FromStream(Stream stream, ContentFormat? format = null, string? name = null, string? mediaType = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream backing a content instance must be readable.", nameof(stream));
        }

        return new StreamContent(stream, format ?? ContentFormat.Unknown, name, mediaType, leaveOpen);
    }

    /// <summary>
    /// Creates a composite from existing content items.
    /// </summary>
    /// <param name="items">The child items, in order.</param>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional composite name.</param>
    /// <param name="leaveItemsOpen">
    /// <see langword="false"/> (the default) to transfer ownership — disposing the composite disposes
    /// every child; <see langword="true"/> to borrow the children and leave them undisposed.
    /// </param>
    /// <returns>A composite over the items. Its <see cref="IContent.OpenRead"/> throws <see cref="NotSupportedException"/> because a pure composite has no serialized form.</returns>
    public static IComposableContent Compose(IReadOnlyList<IContent> items, ContentFormat? format = null, string? name = null, bool leaveItemsOpen = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new CompositeContent(items, format ?? ContentFormat.Unknown, name, leaveItemsOpen);
    }
}
