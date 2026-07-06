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
internal sealed class CompositeContent : IComposableContent
{
    private readonly IReadOnlyList<IContent> _items;
    private readonly bool _leaveItemsOpen;
    private bool _disposed;

    internal CompositeContent(IReadOnlyList<IContent> items, ContentFormat format, string? name, bool leaveItemsOpen)
    {
        _items = items;
        _leaveItemsOpen = leaveItemsOpen;
        Format = format;
        Name = name;
    }

    public string? Name { get; }

    public ContentFormat Format { get; }

    public string? MediaType => null;

    public long? Length => null;

    public bool IsReadOnly => true;

    public bool CanReopen => false;

    public Stream OpenRead() =>
        throw new NotSupportedException("A pure composite has no serialized representation; enumerate its items instead.");

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A pure composite has no serialized representation; enumerate its items instead.");

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
