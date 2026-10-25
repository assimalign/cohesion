using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>The engine-wide function registrations, standard library first, mutable only before build.</summary>
/// <remarks>Application functions cannot replace any standard name. Overloads of an application name must have disjoint arities and one kind. This collection is not thread-safe.</remarks>
[global::System.Diagnostics.DebuggerDisplay("DocumentFunctionCollection, Count = {Count}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentFunctionCollection.DebugView))]
public sealed class DocumentFunctionCollection : IReadOnlyCollection<DocumentFunction>
{
    private readonly List<DocumentFunction> _functions = [];
    private readonly Action? _ensureMutable;
    internal DocumentFunctionCollection(Action? ensureMutable)
    {
        _ensureMutable = ensureMutable;
        foreach (var definition in OqlFunctions.All)
        {
            if (DocumentFunctionCatalog.IsSpecial(definition.Name)) { continue; }
            if (definition.IsAggregate)
            {
                if (definition.Name == "COUNT") { Add(new DocumentBuiltinAggregateFunction(definition, 0)); }
                Add(new DocumentBuiltinAggregateFunction(definition, 1));
            }
            else { Add(new DocumentBuiltinScalarFunction(definition)); }
        }
    }
    /// <summary>Gets the registered function count, overloads included.</summary>
    public int Count => _functions.Count;
    /// <summary>Registers an engine-wide function.</summary><param name="function">The thread-safe function.</param><returns>This collection.</returns>
    /// <exception cref="ArgumentNullException">The function is null.</exception><exception cref="ArgumentException">The name is reserved.</exception><exception cref="InvalidOperationException">Build began, the name changes kind, or arities overlap.</exception>
    public DocumentFunctionCollection Add(DocumentFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        _ensureMutable?.Invoke();
        string name = function.Name.ToString();
        if (function is not (DocumentBuiltinScalarFunction or DocumentBuiltinAggregateFunction) &&
            (OqlFunctions.Find(name) is not null || OqlLanguageProfile.Instance.Keywords.Contains(name, StringComparer.OrdinalIgnoreCase) ||
             DocumentFunctionCatalog.IsReserved(name)))
        { throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionRegistrationReserved, name), nameof(function)); }
        foreach (var registered in _functions)
        {
            if (registered.Name == function.Name &&
                (registered.Kind != function.Kind || registered.MinimumArguments <= function.MaximumArguments && function.MinimumArguments <= registered.MaximumArguments))
            { throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionRegistrationDuplicate, name)); }
        }
        _functions.Add(function);
        return this;
    }
    /// <summary>Checks whether a name has registrations.</summary><param name="name">The name.</param><returns>Whether it is registered.</returns><exception cref="ArgumentException">The name is default.</exception>
    public bool Contains(DocumentFunctionName name)
    {
        if (!DocumentFunctionName.IsValid(name.ToString(), out var error)) { throw new ArgumentException(error, nameof(name)); }
        return _functions.Any(function => function.Name == name);
    }
    /// <summary>Enumerates registrations in order.</summary><returns>The enumerator.</returns>
    public IEnumerator<DocumentFunction> GetEnumerator() => _functions.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal DocumentFunctionCatalog Freeze() => new(_functions);

    private sealed class DebugView
    {
        private readonly DocumentFunctionCollection _target;
        public DebugView(DocumentFunctionCollection value) { _target = value; }
        public int Count => _target.Count;
    }
}
