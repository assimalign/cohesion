using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>The immutable, engine-wide functions visible to every database and session.</summary>
/// <remarks>Lookup is ordinal ignoring case. No registration, dynamic loading or reflection occurs during execution.</remarks>
[global::System.Diagnostics.DebuggerDisplay("DocumentFunctionCatalog, Count = {Count}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentFunctionCatalog.DebugView))]
public sealed class DocumentFunctionCatalog : IReadOnlyCollection<DocumentFunction>
{
    private readonly DocumentFunction[] _functions;
    private readonly FrozenDictionary<string, IReadOnlyList<DocumentFunction>> _names;
    internal DocumentFunctionCatalog(IEnumerable<DocumentFunction> functions)
    {
        _functions = functions.ToArray();
        _names = _functions.GroupBy(function => function.Name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => (IReadOnlyList<DocumentFunction>)Array.AsReadOnly(group.ToArray()), StringComparer.OrdinalIgnoreCase);
    }
    internal static DocumentFunctionCatalog Standard { get; } = new DocumentFunctionCollection(null).Freeze();
    /// <summary>Gets the count, including overloads.</summary>
    public int Count => _functions.Length;
    /// <summary>Gets a name's overloads in registration order.</summary><param name="name">The name.</param><returns>The immutable overloads, empty when absent.</returns><exception cref="ArgumentException">The name is default.</exception>
    public IReadOnlyList<DocumentFunction> GetOverloads(DocumentFunctionName name)
    {
        if (!DocumentFunctionName.IsValid(name.ToString(), out var error)) { throw new ArgumentException(error, nameof(name)); }
        return _names.TryGetValue(name.ToString(), out var functions) ? functions : [];
    }
    /// <summary>Enumerates registrations in order.</summary><returns>The enumerator.</returns>
    public IEnumerator<DocumentFunction> GetEnumerator() => ((IEnumerable<DocumentFunction>)_functions).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal DocumentFunction? Find(string name, int count)
    {
        if (_names.TryGetValue(name, out var functions))
        {
            for (int index = 0; index < functions.Count; index++)
            {
                var function = functions[index];
                if (function.AcceptsArity(count)) { return function; }
            }
        }
        return null;
    }
    internal bool HasName(string name) => _names.ContainsKey(name);
    internal bool IsAggregate(string name) => _names.TryGetValue(name, out var functions) && functions[0].Kind == DocumentFunctionKind.Aggregate;
    internal static bool IsSpecial(string name) => name is "META" or "COALESCE" or "IFMISSING" or "IFNULL" or "IFMISSINGORNULL";
    internal static bool IsReserved(string name) => name.ToUpperInvariant() is
        "ELEMENT" or "FLATTEN" or "STRUCT" or "LIST" or "BAG" or "LISTTOSET" or "TYPEOF" or
        "ARRAY_AGG" or "REGEXP_LIKE" or "REGEXP_REPLACE" or "REGEXP_POSITION";

    private sealed class DebugView
    {
        private readonly DocumentFunctionCatalog _target;
        public DebugView(DocumentFunctionCatalog value) { _target = value; }
        public int Count => _target.Count;
    }
}
