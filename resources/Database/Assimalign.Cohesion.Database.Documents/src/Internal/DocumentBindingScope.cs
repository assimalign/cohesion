namespace Assimalign.Cohesion.Database.Documents.Internal;

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
