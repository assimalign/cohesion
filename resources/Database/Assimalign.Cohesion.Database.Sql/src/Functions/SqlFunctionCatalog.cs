using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The functions a built engine executes, frozen when its build froze the builder's
/// <see cref="SqlFunctionCollection"/>: <see cref="SqlDatabaseEngine.Functions"/>.
/// </summary>
/// <remarks>
/// Immutable, so every session reads it without a lock. A lookup matches names ignoring case and
/// allocates nothing; the planner resolves each call against it once per statement, never per row.
/// There is no run-time registration: NativeAOT could not load a function at run time anyway.
/// </remarks>
public sealed class SqlFunctionCatalog : IReadOnlyCollection<SqlFunction>
{
    private readonly SqlFunction[] _functions;
    private readonly FrozenDictionary<string, SqlFunction[]> _overloads;

    /// <summary>Freezes registrations into a catalog.</summary>
    /// <param name="functions">The registrations, already validated, in registration order.</param>
    internal SqlFunctionCatalog(IEnumerable<SqlFunction> functions)
    {
        _functions = functions.ToArray();
        _overloads = _functions
            .GroupBy(function => function.Name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        HasApplicationFunctions = _functions.Any(function => !SqlStandardLibrary.Contains(function));
    }

    /// <summary>
    /// Gets whether an application registered a function: only such a function can read a call's
    /// cancellation token, so a statement of a catalog without one needs no evaluator to carry it.
    /// </summary>
    internal bool HasApplicationFunctions { get; }

    /// <summary>
    /// Gets the standard library alone: the catalog of an engine created by
    /// <see cref="SqlDatabaseEngine.Create"/>, and of a binding scope outside any engine.
    /// </summary>
    internal static SqlFunctionCatalog Standard { get; } = new SqlFunctionCollection(null).Freeze();

    /// <summary>Gets the number of functions, every overload counted.</summary>
    public int Count => _functions.Length;

    /// <summary>Gets the overloads of a name, in registration order.</summary>
    /// <param name="name">The name, matched ignoring case.</param>
    /// <returns>The overloads; empty when the catalog has none (never null).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public IReadOnlyList<SqlFunction> GetOverloads(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _overloads.TryGetValue(name, out var overloads) ? Array.AsReadOnly(overloads) : [];
    }

    /// <summary>Returns the functions in registration order, the standard library first.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<SqlFunction> GetEnumerator() => ((IEnumerable<SqlFunction>)_functions).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Finds the overloads of a name without allocating.</summary>
    /// <param name="name">The name, matched ignoring case.</param>
    /// <param name="overloads">The overloads, when the catalog has the name.</param>
    /// <returns><see langword="true"/> when the catalog has the name.</returns>
    internal bool TryGetOverloads(string name, out SqlFunction[] overloads)
        => _overloads.TryGetValue(name, out overloads!);

    /// <summary>
    /// Whether a name is an aggregate's. A name carries overloads of one kind only
    /// (<see cref="SqlFunctionCollection.Add"/>), so the planner recognizes an aggregate call by its name.
    /// </summary>
    /// <param name="name">The name as written.</param>
    /// <returns><see langword="true"/> for an aggregate.</returns>
    internal bool IsAggregate(string name)
        => _overloads.TryGetValue(name, out var overloads) && overloads[0].Kind == SqlFunctionKind.Aggregate;
}
