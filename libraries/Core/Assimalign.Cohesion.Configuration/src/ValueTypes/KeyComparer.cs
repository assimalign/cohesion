using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public class KeyComparer : IComparer<Key>, IEqualityComparer<Key>
{
    /// <summary>
    /// Gets a <see cref="StringSegmentComparer"/> object that performs a case-sensitive ordinal <see cref="StringSegment"/> comparison.
    /// </summary>
    public static KeyComparer Ordinal { get; }
        = new KeyComparer(StringComparison.Ordinal, StringComparer.Ordinal);

    /// <summary>
    /// Gets a <see cref="StringSegmentComparer"/> object that performs a case-insensitive ordinal <see cref="StringSegment"/> comparison.
    /// </summary>
    public static KeyComparer OrdinalIgnoreCase { get; }
        = new KeyComparer(StringComparison.OrdinalIgnoreCase, StringComparer.OrdinalIgnoreCase);

    private KeyComparer(StringComparison comparison, StringComparer comparer)
    {
        Comparison = comparison;
        Comparer = comparer;
    }
    private StringComparison Comparison { get; }
    private StringComparer Comparer { get; }

    public int Compare(Key left, Key right)
    {
        var ls = left.Segments;
        var rs = right.Segments;

        var min = Math.Min(ls.Length, rs.Length);

        for (int i = 0; i < min; i++)
        {

        }


        throw new NotImplementedException();
    }

    public bool Equals(Key left, Key right)
    {
        throw new NotImplementedException();
    }

    public int GetHashCode([DisallowNull] Key obj)
    {
        throw new NotImplementedException();
    }
}
