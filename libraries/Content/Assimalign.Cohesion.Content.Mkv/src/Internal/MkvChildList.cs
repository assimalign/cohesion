using System;
using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

// A filtered live view preserves unknown siblings and original node identity.
// Reads never synthesize defaults or mutate the native tree.
[global::System.Diagnostics.DebuggerDisplay("MkvChildList")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(MkvChildList<>.DebugView))]
internal sealed class MkvChildList<T> : IList<T>
{
    private readonly Func<bool, EbmlElement?> _parent;
    private readonly ulong _id;
    private readonly Func<EbmlElement, T> _read;
    private readonly Func<T, EbmlElement> _write;
    private readonly Action? _empty;
    private readonly bool _compareValue;

    internal MkvChildList(Func<bool, EbmlElement?> parent, ulong id, Func<EbmlElement, T> read,
        Func<T, EbmlElement> write, Action? empty = null, bool compareValue = false)
    {
        _parent = parent; _id = id; _read = read; _write = write; _empty = empty; _compareValue = compareValue;
    }

    public int Count
    {
        get
        {
            var count = 0;
            foreach (var child in MkvElements.All(_parent(false), _id)) { count++; }
            return count;
        }
    }
    public bool IsReadOnly => false;
    public T this[int index]
    {
        get { var parent = _parent(false); var native = NativeIndex(parent, index); return _read(parent!.Children[native]); }
        set
        {
            var element = Make(value);
            var parent = _parent(false);
            var native = NativeIndex(parent, index);
            if (_compareValue && Equals(parent!.Children[native].Value, element.Value)) { return; }
            parent!.Children[native] = element;
        }
    }
    public void Add(T item) { var element = Make(item); _parent(true)!.Children.Add(element); }
    public void Insert(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
        var element = Make(item);
        var parent = _parent(true)!;
        var native = index == Count ? parent.Children.Count : NativeIndex(parent, index);
        parent.Children.Insert(native, element);
    }
    public void Clear()
    {
        if (_parent(false) is not { } parent) { return; }
        for (var i = parent.Children.Count - 1; i >= 0; i--)
        {
            if (parent.Children[i].Identifier.EncodedValue == _id) { parent.Children.RemoveAt(i); }
        }
        _empty?.Invoke();
    }
    public int IndexOf(T item)
    {
        if (item is null) { return -1; }
        var element = _write(item);
        var index = 0;
        foreach (var child in MkvElements.All(_parent(false), _id))
        {
            if (_compareValue ? Equals(child.Value, element.Value) : ReferenceEquals(child, element)) { return index; }
            index++;
        }
        return -1;
    }
    public bool Contains(T item) => IndexOf(item) >= 0;
    public bool Remove(T item)
    {
        var index = IndexOf(item);
        if (index < 0) { return false; }
        RemoveAt(index);
        return true;
    }
    public void RemoveAt(int index)
    {
        var parent = _parent(false);
        var native = NativeIndex(parent, index);
        parent!.Children.RemoveAt(native);
        if (Count == 0) { _empty?.Invoke(); }
    }
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(arrayIndex, array.Length);
        if (Count > array.Length - arrayIndex) { throw new ArgumentException(ErrorMessages.CollectionCapacity, nameof(array)); }
        foreach (var item in this) { array[arrayIndex++] = item; }
    }
    public IEnumerator<T> GetEnumerator()
    {
        foreach (var child in MkvElements.All(_parent(false), _id)) { yield return _read(child); }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private EbmlElement Make(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _write(item);
    }
    private int NativeIndex(EbmlElement? parent, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (parent is not null)
        {
            for (var i = 0; i < parent.Children.Count; i++)
            {
                if (parent.Children[i].Identifier.EncodedValue == _id && index-- == 0) { return i; }
            }
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    private sealed class DebugView
    {
        private readonly MkvChildList<T> _target;

        public DebugView(MkvChildList<T> value)
        {
            _target = value;
        }

        public bool IsReadOnly => _target.IsReadOnly;
    }
}
