using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>The immutable engine-wide scalar catalog, fixed before storage or workers are created.</summary>
[DebuggerDisplay("KeyValueFunctionCatalog, Count = {Count}")]
[DebuggerTypeProxy(typeof(KeyValueFunctionCatalog.DebugView))]
public sealed class KeyValueFunctionCatalog : IReadOnlyCollection<KeyValueFunction>
{
    private readonly KeyValueFunction[] _functions;
    private readonly FrozenDictionary<string, KeyValueFunction[]> _overloads;

    internal KeyValueFunctionCatalog(IEnumerable<KeyValueFunction> functions)
    {
        _functions = functions.ToArray();
        _overloads = _functions.GroupBy(function => function.Name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    internal static KeyValueFunctionCatalog Empty { get; } = new([]);

    /// <summary>Gets the number of registered overloads.</summary>
    public int Count => _functions.Length;

    /// <summary>Looks up overloads without exposing mutable catalog arrays.</summary>
    /// <param name="name">The identifier, matched ignoring case; default is refused.</param>
    /// <returns>The overloads, empty when absent and never null.</returns>
    /// <exception cref="ArgumentException">The name is default.</exception>
    public IReadOnlyList<KeyValueFunction> GetOverloads(KeyValueFunctionName name)
    {
        if (name.Value is null) { throw new ArgumentException(ErrorMessages.FunctionNameRequired, nameof(name)); }
        return _overloads.TryGetValue(name.ToString(), out var functions) ? Array.AsReadOnly(functions) : [];
    }

    /// <summary>Enumerates the catalog in registration order.</summary>
    /// <returns>The registration enumerator.</returns>
    public IEnumerator<KeyValueFunction> GetEnumerator() => ((IEnumerable<KeyValueFunction>)_functions).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal KeyValueFunction Resolve(KeyValueFunctionName name, ReadOnlySpan<object?> arguments)
    {
        if (!_overloads.TryGetValue(name.ToString(), out var overloads))
        {
            throw DatabaseException.CreateKeyValuePairFailure("CV01018", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionUnknown, name));
        }
        bool countMatched = false;
        KeyValueFunction? match = null;
        foreach (KeyValueFunction function in overloads)
        {
            var parameters = function.ParameterTypes;
            if (parameters.Length != arguments.Length) { continue; }
            countMatched = true;
            bool accepts = true;
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index] is null) { continue; }
                if (!KeyValueFunctionValues.TryGetType(arguments[index], out DatabaseType type) || type != parameters[index])
                {
                    accepts = false;
                    break;
                }
            }
            if (!accepts) { continue; }
            if (match is not null) { throw DatabaseException.CreateKeyValuePairFailure("CV01021", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionAmbiguous, name)); }
            match = function;
        }
        if (match is not null) { return match; }
        throw countMatched
            ? DatabaseException.CreateKeyValuePairFailure("CV01020", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionTypesMismatch, name))
            : DatabaseException.CreateKeyValuePairFailure("CV01019", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.FunctionArityMismatch, name, arguments.Length));
    }

    private sealed class DebugView
    {
        private readonly KeyValueFunctionCatalog _target;

        public DebugView(KeyValueFunctionCatalog value) { _target = value; }

        public int Count => _target.Count;
    }
}
