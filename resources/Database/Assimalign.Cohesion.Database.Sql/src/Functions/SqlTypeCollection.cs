using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The SQL types an engine under construction knows: its builder's
/// <see cref="SqlDatabaseEngineBuilder.Types"/>, holding the built-in storage types.
/// </summary>
/// <remarks>
/// In phase E2 the collection lists the built-in types that functions declare
/// (<see cref="SqlType.Boolean"/> to <see cref="SqlType.Jsonb"/>); the pseudo-types are not storage
/// types and are not listed. Domains and the cast registry, the only types an application adds,
/// arrive with phase E3, together with the members that add them.
/// </remarks>
public sealed class SqlTypeCollection : IReadOnlyCollection<SqlType>
{
    private readonly SqlType[] _types;

    /// <summary>Initializes the collection of the built-in types.</summary>
    internal SqlTypeCollection()
    {
        _types = SqlType.BuiltIn;
    }

    /// <summary>Gets the number of types.</summary>
    public int Count => _types.Length;

    /// <summary>Whether a type of a name is known, ignoring case.</summary>
    /// <param name="name">The SQL name, for example <c>BIGINT</c>.</param>
    /// <returns><see langword="true"/> when the collection has the type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var type in _types)
        {
            if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the types in declaration order.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<SqlType> GetEnumerator() => ((IEnumerable<SqlType>)_types).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
