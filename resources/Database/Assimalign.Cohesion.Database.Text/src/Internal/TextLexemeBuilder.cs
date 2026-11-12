using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Text.Internal;

internal sealed class TextLexemeBuilder
{
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    private readonly List<(TextPosition Position, TextWeight FirstWeight)> _positions = new();

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
            if (_positions[mid].Position.Position < position.Position)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        if (low < _positions.Count && _positions[low].Position.Position == position.Position)
        {
            if (_positions[low].Position.Weight < position.Weight)
            {
                _positions[low] = (position, _positions[low].FirstWeight);
            }
        }
        else if (low < limit)
        {
            if (_positions.Count == limit)
            {
                _positions.RemoveAt(limit - 1);
            }

            _positions.Insert(low, (position, position.Weight));
        }
    }

    internal TextPosition[] Snapshot(int limit = TextVector.MaxPositionsPerLexeme)
    {
        var positions = new TextPosition[_positions.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = _positions[i].Position;
        }

        // PostgreSQL uniquePos stops on arrival at a new final retained position,
        // before inspecting further duplicates there. Stable input order makes
        // that cutoff deterministic; a sole position still merges its weights.
        if (positions.Length > 1 && (positions.Length == limit || positions[^1].Position == TextVector.MaxPosition))
        {
            positions[^1] = TextPosition.Create(positions[^1].Position, _positions[^1].FirstWeight);
        }

        return positions;
    }
}
