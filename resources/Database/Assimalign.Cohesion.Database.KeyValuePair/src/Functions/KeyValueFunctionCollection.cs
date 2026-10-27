using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>The engine builder's scalar registrations, frozen when its build begins.</summary>
/// <remarks>Names compare ignoring case. The collection has no removal and is not thread-safe.</remarks>
[DebuggerDisplay("KeyValueFunctionCollection, Count = {Count}")]
[DebuggerTypeProxy(typeof(KeyValueFunctionCollection.DebugView))]
public sealed class KeyValueFunctionCollection : IReadOnlyCollection<KeyValueFunction>
{
    private readonly List<KeyValueFunction> _functions = [];
    private readonly Action _ensureMutable;

    internal KeyValueFunctionCollection(Action ensureMutable)
    {
        _ensureMutable = ensureMutable;
    }

    /// <summary>Gets the number of registered overloads.</summary>
    public int Count => _functions.Count;

    /// <summary>Registers one thread-safe function instance for all engine sessions.</summary>
    /// <param name="function">The function to register.</param>
    /// <returns>This collection.</returns>
    /// <exception cref="ArgumentNullException">The function is null.</exception>
    /// <exception cref="ArgumentException">Its name is a reserved command.</exception>
    /// <exception cref="InvalidOperationException">Build began, or the same name and signature are registered.</exception>
    public KeyValueFunctionCollection Add(KeyValueFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        _ensureMutable();
        string name = function.Name.ToString();
        if (name.ToUpperInvariant() is "GET" or "PUT" or "DELETE" or "EXISTS" or "SCAN" or "KEYSPACES" or "CALL")
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.FunctionNameReserved, name), nameof(function));
        }
        foreach (KeyValueFunction existing in _functions)
        {
            if (existing.Name == function.Name && existing.ParameterTypes.SequenceEqual(function.ParameterTypes))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.FunctionSignatureDuplicate, name));
            }
        }
        _functions.Add(function);
        return this;
    }

    /// <summary>Checks whether a name has any registered overload.</summary>
    /// <param name="name">The identifier; default is refused.</param>
    /// <returns>Whether the collection contains the name.</returns>
    /// <exception cref="ArgumentException">The name is default.</exception>
    public bool Contains(KeyValueFunctionName name)
    {
        if (name.Value is null) { throw new ArgumentException(ErrorMessages.FunctionNameRequired, nameof(name)); }
        foreach (KeyValueFunction function in _functions)
        {
            if (function.Name == name) { return true; }
        }
        return false;
    }

    /// <summary>Enumerates registrations in their original order.</summary>
    /// <returns>The registration enumerator.</returns>
    public IEnumerator<KeyValueFunction> GetEnumerator() => _functions.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal KeyValueFunctionCatalog Freeze() => new(_functions);

    private sealed class DebugView
    {
        private readonly KeyValueFunctionCollection _target;

        public DebugView(KeyValueFunctionCollection value) { _target = value; }

        public int Count => _target.Count;
    }
}
