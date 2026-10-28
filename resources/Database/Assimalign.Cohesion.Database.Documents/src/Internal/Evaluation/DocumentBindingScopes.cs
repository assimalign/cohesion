using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// One lexical frame per binding expression and enclosing scope, reused throughout planning.
[global::System.Diagnostics.DebuggerDisplay("DocumentBindingScopes")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentBindingScopes.DebugView))]
internal sealed class DocumentBindingScopes
{
    private readonly Dictionary<(OqlExpression, DocumentBindingScope?), DocumentBindingScope> _scopes = new();

    internal void RegisterGroup(OqlSelectExpression query, DocumentBindingScope scope) => _scopes.Add((query, null), scope);

    internal DocumentBindingScope? FindGroup(OqlSelectExpression query) => _scopes.GetValueOrDefault((query, null));

    internal DocumentBindingScope Enter(OqlExpression expression, string variable, DocumentBindingScope? parent)
    {
        var key = (expression, parent);
        if (!_scopes.TryGetValue(key, out var scope))
        {
            scope = new DocumentBindingScope(variable, parent);
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
