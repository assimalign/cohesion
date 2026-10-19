using System.Text.Json;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Describes one executable OQL built-in function.</summary>
public sealed class OqlFunction
{
    internal OqlFunction(string name, int minimumArguments, int maximumArguments, DatabaseType resultType,
        bool aggregate = false, params JsonValueKind[] argumentTypes)
    {
        Name = name;
        MinimumArguments = minimumArguments;
        MaximumArguments = maximumArguments;
        ResultType = resultType;
        IsAggregate = aggregate;
        _argumentTypes = argumentTypes;
    }

    private readonly JsonValueKind[] _argumentTypes;

    /// <summary>Gets the invariant upper-case name.</summary>
    public string Name { get; }
    /// <summary>Gets the minimum argument count.</summary>
    public int MinimumArguments { get; }
    /// <summary>Gets the maximum argument count, or Int32.MaxValue for a variadic function.</summary>
    public int MaximumArguments { get; }
    /// <summary>Gets the static result type; Null denotes a result determined by the arguments.</summary>
    public DatabaseType ResultType { get; }
    /// <summary>Gets whether the call consumes a group of documents.</summary>
    public bool IsAggregate { get; }

    /// <summary>Checks the number of arguments, including COUNT's star argument.</summary>
    /// <param name="count">The argument count.</param>
    /// <returns>Whether the count is accepted.</returns>
    public bool AcceptsArity(int count) => count >= MinimumArguments && count <= MaximumArguments;

    /// <summary>Checks a known JSON argument kind. NULL and MISSING propagation belongs to the caller.</summary>
    /// <param name="index">The zero-based argument index.</param>
    /// <param name="kind">The known, non-null argument kind.</param>
    /// <returns>Whether the function accepts the kind at this position.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">The index is negative.</exception>
    public bool AcceptsArgument(int index, JsonValueKind kind)
    {
        System.ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (_argumentTypes.Length == 0) { return true; }
        var expected = _argumentTypes[System.Math.Min(index, _argumentTypes.Length - 1)];
        return expected == JsonValueKind.Undefined || expected == kind;
    }
}
