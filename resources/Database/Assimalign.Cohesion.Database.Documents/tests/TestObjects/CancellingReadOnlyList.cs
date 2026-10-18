using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Documents.Tests;

internal sealed class CancellingReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly CancellationTokenSource _cancellation;

    internal CancellingReadOnlyList(IReadOnlyList<T> items, CancellationTokenSource cancellation)
    {
        _items = items;
        _cancellation = cancellation;
    }

    internal int ReadCount { get; private set; }
    public int Count => _items.Count;
    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator()
    {
        foreach (var item in _items)
        {
            ReadCount++;
            _cancellation.Cancel();
            yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
