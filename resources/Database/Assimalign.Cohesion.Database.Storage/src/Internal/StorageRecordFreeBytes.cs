using System.Collections.Generic;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Internal;

// Caller holds Storage's owner lock. Nodes are allocated once per page; changing
// free bytes moves the existing node in its owner's maximum heap without allocation.
internal sealed class StorageRecordFreeBytes
{
    private readonly Dictionary<long, Entry> _pages = new();
    private readonly Dictionary<ulong, List<Entry>> _owners = new();

    internal void Register(ulong owner, long page, int? capacity = null)
    {
        if (_pages.TryGetValue(page, out var existing) && existing.Owner == owner)
        {
            Change(existing, capacity ?? SlottedPage.MaxRecordSize, capacity.HasValue);
            return;
        }
        Remove(page);
        if (!_owners.TryGetValue(owner, out var heap)) { _owners.Add(owner, heap = new()); }
        var entry = new Entry(owner, page, capacity ?? SlottedPage.MaxRecordSize, capacity.HasValue, heap.Count);
        _pages.Add(page, entry);
        heap.Add(entry);
        Up(heap, entry.Index);
    }

    internal bool IsUnknown(long page) => _pages.TryGetValue(page, out var entry) && !entry.Known;

    internal void MarkUnknown(long page)
    {
        if (_pages.TryGetValue(page, out var entry)) { Change(entry, SlottedPage.MaxRecordSize, false); }
    }

    internal void Measure(long page, int capacity, bool nestedWriter)
    {
        if (!_pages.TryGetValue(page, out var entry)) { return; }
        if (nestedWriter) { MarkUnknown(page); }
        else { Change(entry, capacity, true); }
    }

    internal void Adjust(long page, int difference, bool nestedWriter)
    {
        if (!_pages.TryGetValue(page, out var entry)) { return; }
        if (nestedWriter) { MarkUnknown(page); }
        else if (entry.Known) { Change(entry, entry.Capacity + difference, true); }
    }

    internal bool TryGet(ulong owner, int minimum, HashSet<long>? excluded, out long page)
    {
        if (_owners.TryGetValue(owner, out var heap)) { return Find(heap, 0, minimum, excluded, out page); }
        page = 0;
        return false;
    }

    private static bool Find(List<Entry> heap, int index, int minimum, HashSet<long>? excluded, out long page)
    {
        page = 0;
        if (index >= heap.Count || heap[index].Capacity < minimum) { return false; }
        if (excluded is null || !excluded.Contains(heap[index].Page)) { page = heap[index].Page; return true; }
        return Find(heap, (index * 2) + 1, minimum, excluded, out page)
            || Find(heap, (index * 2) + 2, minimum, excluded, out page);
    }

    internal void Remove(long page)
    {
        if (!_pages.Remove(page, out var entry)) { return; }
        var heap = _owners[entry.Owner];
        int index = entry.Index, last = heap.Count - 1;
        if (index != last) { heap[index] = heap[last]; heap[index].Index = index; }
        heap.RemoveAt(last);
        if (heap.Count == 0) { _owners.Remove(entry.Owner); }
        else if (index < heap.Count) { Reorder(heap, index); }
    }

    private void Change(Entry entry, int capacity, bool known)
    {
        if (entry.Capacity == capacity && entry.Known == known) { return; }
        entry.Capacity = capacity;
        entry.Known = known;
        Reorder(_owners[entry.Owner], entry.Index);
    }

    private static void Reorder(List<Entry> heap, int index)
    {
        if (index > 0 && Before(heap[index], heap[(index - 1) / 2])) { Up(heap, index); }
        else { Down(heap, index); }
    }

    private static bool Before(Entry left, Entry right)
        => left.Capacity > right.Capacity || left.Capacity == right.Capacity && left.Page < right.Page;

    private static void Up(List<Entry> heap, int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!Before(heap[index], heap[parent])) { break; }
            Swap(heap, index, parent);
            index = parent;
        }
    }

    private static void Down(List<Entry> heap, int index)
    {
        while ((index * 2) + 1 < heap.Count)
        {
            int child = (index * 2) + 1;
            if (child + 1 < heap.Count && Before(heap[child + 1], heap[child])) { child++; }
            if (!Before(heap[child], heap[index])) { break; }
            Swap(heap, index, child);
            index = child;
        }
    }

    private static void Swap(List<Entry> heap, int left, int right)
    {
        (heap[left], heap[right]) = (heap[right], heap[left]);
        heap[left].Index = left;
        heap[right].Index = right;
    }

    private sealed class Entry
    {
        internal readonly ulong Owner;
        internal readonly long Page;
        internal int Capacity;
        internal bool Known;
        internal int Index;

        internal Entry(ulong owner, long page, int capacity, bool known, int index)
        {
            Owner = owner;
            Page = page;
            Capacity = capacity;
            Known = known;
            Index = index;
        }
    }
}
