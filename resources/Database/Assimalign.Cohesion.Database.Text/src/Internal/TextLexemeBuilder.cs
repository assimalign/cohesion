using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Text.Internal;

internal sealed class TextLexemeBuilder
{
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    private readonly List<TextPosition> _positions = new();

    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    internal int Count => _positions.Count;

    internal void Add(TextPosition position, int limit)
    {
        // Default structs have not passed the factory's validation.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(position.Position);
        int low = 0, high = _positions.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_positions[mid].Position < position.Position)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        if (low < _positions.Count && _positions[low].Position == position.Position)
        {
            if (_positions[low].Weight < position.Weight)
            {
                _positions[low] = position;
            }
        }
        else if (low < limit)
        {
            if (_positions.Count == limit)
            {
                _positions.RemoveAt(limit - 1);
            }

            _positions.Insert(low, position);
        }
    }

    internal TextPosition[] Snapshot()
    {
        var positions = new TextPosition[_positions.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = _positions[i];
        }

        return positions;
    }
}
