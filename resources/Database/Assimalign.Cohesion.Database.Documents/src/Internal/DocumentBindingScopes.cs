using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// One lexical frame per quantifier and enclosing scope, reused throughout planning.
internal sealed class DocumentBindingScopes
{
    private readonly Dictionary<(OqlQuantifiedExpression, DocumentBindingScope?), DocumentBindingScope> _scopes = new();

    internal DocumentBindingScope Enter(OqlQuantifiedExpression expression, DocumentBindingScope? parent)
    {
        var key = (expression, parent);
        if (!_scopes.TryGetValue(key, out var scope))
        {
            scope = new DocumentBindingScope(expression.Variable, parent);
            _scopes.Add(key, scope);
        }
        return scope;
    }
}

internal sealed class DocumentBindingScope
{
    private readonly string _name;
    private readonly DocumentBindingScope? _parent;

    internal DocumentBindingScope(string name, DocumentBindingScope? parent)
    {
        _name = name;
        _parent = parent;
    }

    internal static DocumentBindingScope? Resolve(DocumentBindingScope? scope, string? name, out int distance)
    {
        distance = 0;
        for (; scope is not null; scope = scope._parent, distance++)
        {
            if (scope._name == name) { return scope; }
        }
        return null;
    }
}
