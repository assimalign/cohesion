using System;
using System.Diagnostics;
using System.Numerics;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The member or item positions of one instance that keywords evaluated: the annotations
/// <c>unevaluatedProperties</c> and <c>unevaluatedItems</c> read (2020-12 Core §11).
/// </summary>
/// <remarks>
/// Members are numbered by their position in the object, so a position set works for both kinds. A
/// set belongs to one frame and is cleared when the frame is reused.
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class EvaluatedSet
{
    private ulong[] _bits = [];

    // Every word at or past this index is zero.
    private int _usedWords;

    /// <summary>Gets the number of positions in the set.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _usedWords; i++)
            {
                count += BitOperations.PopCount(_bits[i]);
            }

            return count;
        }
    }

    public void Clear()
    {
        if (_usedWords > 0)
        {
            Array.Clear(_bits, 0, _usedWords);
            _usedWords = 0;
        }
    }

    public void Add(int position)
    {
        int word = position >> 6;
        Reserve(word + 1);
        _bits[word] |= 1UL << (position & 63);
    }

    /// <summary>Adds the positions from <paramref name="start"/> up to, not including, <paramref name="end"/>.</summary>
    public void AddRange(int start, int end)
    {
        if (start >= end)
        {
            return;
        }

        Reserve(((end - 1) >> 6) + 1);
        for (int position = start; position < end; position++)
        {
            _bits[position >> 6] |= 1UL << (position & 63);
        }
    }

    public bool Contains(int position)
    {
        int word = position >> 6;
        return word < _usedWords && (_bits[word] & (1UL << (position & 63))) != 0;
    }

    public void UnionWith(EvaluatedSet other)
    {
        if (other._usedWords == 0)
        {
            return;
        }

        Reserve(other._usedWords);
        for (int i = 0; i < other._usedWords; i++)
        {
            _bits[i] |= other._bits[i];
        }
    }

    private void Reserve(int words)
    {
        if (words > _bits.Length)
        {
            Array.Resize(ref _bits, Math.Max(words, Math.Max(4, _bits.Length * 2)));
        }

        if (words > _usedWords)
        {
            _usedWords = words;
        }
    }

    private sealed class DebugView
    {
        private readonly EvaluatedSet _set;

        public DebugView(EvaluatedSet set)
        {
            _set = set;
        }

        public int Count => _set.Count;
    }
}
