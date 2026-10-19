using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

// Cancels on an observed work count, on the executing thread, without a timing race.
internal sealed class CancelingReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly CancellationTokenSource _cancellation;
    private readonly int _cancelAt;

    internal CancelingReadOnlyList(IReadOnlyList<T> items, CancellationTokenSource cancellation, int cancelAt)
    { _items = items; _cancellation = cancellation; _cancelAt = cancelAt; }

    internal int Reads { get; private set; }
    public int Count => _items.Count;
    public T this[int index]
    {
        get
        {
            if (++Reads == _cancelAt) { _cancellation.Cancel(); }
            return _items[index];
        }
    }

    public IEnumerator<T> GetEnumerator()
    { for (int i = 0; i < Count; i++) { yield return this[i]; } }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
