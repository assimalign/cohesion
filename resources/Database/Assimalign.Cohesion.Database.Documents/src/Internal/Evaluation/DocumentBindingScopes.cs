using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// One lexical frame per quantifier and enclosing scope, reused throughout planning.
[global::System.Diagnostics.DebuggerDisplay("DocumentBindingScopes")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentBindingScopes.DebugView))]
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentBindingScopes value)
        {
            _ = value;
        }
    }
}
