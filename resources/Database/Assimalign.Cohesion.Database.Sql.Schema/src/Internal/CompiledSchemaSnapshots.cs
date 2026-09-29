using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

internal static class CompiledSchemaSnapshots
{
    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new T[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] is null)
            {
                throw new ArgumentException("Compiled schema collections cannot contain null values.", nameof(values));
            }

            copy[index] = values[index];
        }

        return Array.AsReadOnly(copy);
    }

    internal static IReadOnlyList<T> CopySorted<T>(
        IReadOnlyList<T> values,
        Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        IReadOnlyList<T> snapshot = Copy(values);
        var copy = new T[snapshot.Count];
        for (int index = 0; index < snapshot.Count; index++)
        {
            copy[index] = snapshot[index];
        }

        Array.Sort(copy, comparison);
        return Array.AsReadOnly(copy);
    }
}
