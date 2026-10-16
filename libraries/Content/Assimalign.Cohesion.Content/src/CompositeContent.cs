using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// A pure composite assembled from existing items. It has no serialized representation of its own; the
/// items carry the data. Children are owned (disposed with the composite) unless borrowed at creation.
/// </summary>
public sealed class CompositeContent : IComposableContent
{
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
    /// <exception cref="ArgumentNullException">The items is null.</exception>
    public static CompositeContent Create(IReadOnlyList<IContent> items, ContentFormat? format = null, string? name = null, bool leaveItemsOpen = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new CompositeContent(items, format ?? ContentFormat.Unknown, name, leaveItemsOpen);
    }

    private readonly IReadOnlyList<IContent> _items;
    private readonly bool _leaveItemsOpen;
    private bool _disposed;

    private CompositeContent(IReadOnlyList<IContent> items, ContentFormat format, string? name, bool leaveItemsOpen)
    {
        _items = items;
        _leaveItemsOpen = leaveItemsOpen;
        Format = format;
        Name = name;
    }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public ContentFormat Format { get; }

    /// <inheritdoc/>
    public string? MediaType => null;

    /// <inheritdoc/>
    public long? Length => null;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool CanReopen => false;

    /// <inheritdoc/>
    public Stream OpenRead() =>
        throw new NotSupportedException(ErrorMessages.CompositeNotSerialized);

    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.CompositeNotSerialized);

    /// <inheritdoc/>
    public async IAsyncEnumerable<IContent> GetItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.CompletedTask.ConfigureAwait(false);

        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveItemsOpen)
        {
            foreach (var item in _items)
            {
                item.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveItemsOpen)
        {
            foreach (var item in _items)
            {
                await item.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
