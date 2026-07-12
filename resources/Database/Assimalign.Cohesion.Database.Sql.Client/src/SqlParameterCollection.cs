using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// The named parameters bound to a <see cref="SqlCommand"/>.
/// </summary>
/// <remarks>
/// Parameters bind by bare name — the SQL dialect's <c>@</c>/<c>$</c> sigil is
/// presentation only. Names are normalized on add (a leading <c>@</c> or <c>$</c>
/// is stripped) so callers may write <c>Add("@id", 1)</c> or <c>Add("id", 1)</c>
/// interchangeably and both bind the <c>@id</c> placeholder.
/// </remarks>
public sealed class SqlParameterCollection
{
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the number of bound parameters.
    /// </summary>
    public int Count => _parameters.Count;

    /// <summary>
    /// Gets or sets a parameter value by name.
    /// </summary>
    /// <param name="name">The parameter name, with or without a leading sigil.</param>
    /// <returns>The bound value, or null.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="KeyNotFoundException">Thrown by the getter when no parameter carries the name.</exception>
    public object? this[string name]
    {
        get => _parameters[Normalize(name)];
        set => _parameters[Normalize(name)] = value;
    }

    /// <summary>
    /// Adds or replaces a parameter value.
    /// </summary>
    /// <param name="name">The parameter name, with or without a leading sigil.</param>
    /// <param name="value">The value to bind; null binds a SQL null.</param>
    /// <returns>This collection, for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or whitespace.</exception>
    public SqlParameterCollection Add(string name, object? value)
    {
        _parameters[Normalize(name)] = value;
        return this;
    }

    /// <summary>
    /// Removes all parameters.
    /// </summary>
    public void Clear() => _parameters.Clear();

    /// <summary>
    /// Gets the bound parameters as a read-only, bare-name-keyed view for the wire.
    /// </summary>
    /// <returns>The parameters, or null when none are bound.</returns>
    internal IReadOnlyDictionary<string, object?>? AsWireParameters()
        => _parameters.Count == 0 ? null : _parameters;

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return name[0] is '@' or '$' ? name[1..] : name;
    }
}
