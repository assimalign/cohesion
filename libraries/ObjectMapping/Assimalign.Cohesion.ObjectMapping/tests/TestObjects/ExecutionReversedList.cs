using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>A List subclass that deliberately supplies a different interface enumeration order.</summary>
public sealed class ExecutionReversedList : List<ExecutionSource>, IEnumerable<ExecutionSource>
{
    IEnumerator<ExecutionSource> IEnumerable<ExecutionSource>.GetEnumerator()
    {
        for (int i = Count - 1; i >= 0; i--)
        {
            yield return this[i];
        }
    }
}
