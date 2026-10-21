using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphCompositeValues
{
    internal static bool IsComposite(object? value) => value is IList and not byte[] or IReadOnlyDictionary<string, object?>;

    internal static object? Snapshot(object? value, Location? location = null)
    {
        int count = 0;
        try { var copy = Copy(value, 0, ref count); GraphValueCodec.Measure(copy); return copy; }
        catch (Assimalign.Cohesion.Database.Types.DatabaseTypeException error)
        { throw new GraphPlanningException("COHDBG003", error.Message, location, error); }
        catch (GraphPlanningException error) when (location is not null)
        { throw error.At(location); }
    }

    private static object? Copy(object? value, int depth, ref int count)
    {
        if (depth > GraphValueCodec.MaxDepth || ++count > GraphValueCodec.MaxValueCount)
        { throw new GraphPlanningException("COHDBG003", ErrorMessages.CompositeLimit); }
        if (value is Array array && (array.Rank != 1 || array.GetLowerBound(0) != 0))
        { throw new GraphPlanningException("COHDBG003", ErrorMessages.CompositeRequired); }
        if (value is IReadOnlyDictionary<string, object?> map)
        {
            if (map.Count > GraphValueCodec.MaxValueCount - count)
            { throw new GraphPlanningException("COHDBG003", ErrorMessages.CompositeLimit); }
            var copy = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
            foreach (var entry in map) { copy.Add(entry.Key, Copy(entry.Value, depth + 1, ref count)); }
            return new ReadOnlyDictionary<string, object?>(copy);
        }
        if (value is IList list && value is not byte[])
        {
            if (list.Count > GraphValueCodec.MaxValueCount - count)
            { throw new GraphPlanningException("COHDBG003", ErrorMessages.CompositeLimit); }
            var copy = new object?[list.Count];
            for (int i = 0; i < copy.Length; i++) { copy[i] = Copy(list[i], depth + 1, ref count); }
            return copy;
        }
        try { GraphDatabase.TypeOf(value); }
        catch (DatabaseException error)
        { throw new GraphPlanningException("COHDBG003", ErrorMessages.ParameterScalarRequired, null, error); }
        if (value is double number && !double.IsFinite(number) || value is float single && !float.IsFinite(single))
        { throw new GraphPlanningException("COHDBG003", ErrorMessages.ParameterScalarRequired); }
        return value;
    }

    // FALSE dominates UNKNOWN, including when an earlier field compared as UNKNOWN.
    internal static bool? Equal(object? left, object? right, bool grouping = false)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (left is null || right is null) { return grouping ? left is null && right is null : null; }
        IEnumerable<(object? Left, object? Right)>? pairs = null;
        if (left is object?[] a && right is object?[] b)
        {
            if (a.Length != b.Length) { return false; }
            pairs = a.Zip(b, (x, y) => (x, y));
        }
        else if (left is IReadOnlyDictionary<string, object?> x && right is IReadOnlyDictionary<string, object?> y)
        {
            if (x.Count != y.Count || x.Keys.Any(key => !y.ContainsKey(key))) { return false; }
            pairs = x.Select(entry => (entry.Value, y[entry.Key]));
        }
        if (pairs is null) { return GraphValueComparer.Instance.Equals(left, right); }
        bool unknown = false;
        foreach (var pair in pairs)
        {
            bool? equal = Equal(pair.Left, pair.Right, grouping);
            if (equal == false) { return false; }
            unknown |= equal is null;
        }
        return unknown ? null : true;
    }
}
