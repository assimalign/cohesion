using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The base of <c>$ref</c>, <c>$dynamicRef</c> and <c>$recursiveRef</c>: a URI reference resolved
/// against its schema's base URI, whose target is found the first time it is followed.
/// </summary>
/// <remarks>
/// Resolution is deferred so a document may refer to resources registered after it loads. The target
/// is cached; concurrent first uses resolve to the same schema.
/// </remarks>
[DebuggerDisplay("{Name,nq}: {Reference}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal abstract class ReferenceKeyword : SchemaKeyword
{
    private SchemaNode? _target;

    protected ReferenceKeyword(SchemaNode owner, string name, JsonElement value, UriReference reference)
        : base(owner, name, value)
    {
        Reference = reference;
    }

    /// <summary>Gets the reference, resolved against the base URI of its schema.</summary>
    public UriReference Reference { get; }

    public override int Priority => KeywordPriority.Reference;

    /// <summary>Returns the schema the reference names, resolving it on first use.</summary>
    /// <exception cref="JsonSchemaException">No known resource or location matches the reference.</exception>
    public SchemaNode GetTarget()
    {
        SchemaNode? target = Volatile.Read(ref _target);
        if (target is null)
        {
            target = Resolve();
            Volatile.Write(ref _target, target);
        }

        return target;
    }

    protected bool Apply(EvaluationContext context, SchemaNode target, JsonElement instance)
    {
        if (context.EvaluateReference(this, target, instance))
        {
            return true;
        }

        context.Fail(Resources.ReferenceFailed);
        return false;
    }

    private SchemaNode Resolve()
    {
        SchemaResource owner = Owner.Resource;
        JsonSchemaDocument document = owner.Document;
        string uri = Reference.ToNormalizedString(includeFragment: false);
        if (!document.TryGetLocalResource(uri, out SchemaResource? resource)
            && !document.Registry.TryGetResource(uri, owner.Dialect, out resource))
        {
            throw Unresolved();
        }

        return resource.Document.ResolveFragment(resource, Reference.Fragment) ?? throw Unresolved();
    }

    private JsonSchemaProcessingException Unresolved() =>
        JsonSchemaProcessingException.ReferenceUnresolved(Reference.ToString(), Owner.Document.FormatLocation(Owner.Pointer + PathSegment));

    private sealed class DebugView
    {
        private readonly ReferenceKeyword _keyword;

        public DebugView(ReferenceKeyword keyword)
        {
            _keyword = keyword;
        }

        public string Name => _keyword.Name;

        public UriReference Reference => _keyword.Reference;

        public SchemaNode Owner => _keyword.Owner;
    }
}
