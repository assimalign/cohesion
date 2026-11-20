using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>$dynamicRef</c> (2020-12 Core §8.2.3.2): a reference whose target moves to the outermost
/// resource in the dynamic scope that declares the same <c>$dynamicAnchor</c>.
/// </summary>
/// <remarks>
/// The reference is dynamic only when its fragment is a plain name and the schema it initially
/// resolves to declares that name with <c>$dynamicAnchor</c> (the "bookend"); otherwise it behaves
/// like <c>$ref</c>.
/// </remarks>
internal sealed class DynamicRefKeyword : ReferenceKeyword
{
    public DynamicRefKeyword(SchemaNode owner, string name, JsonElement value, UriReference reference)
        : base(owner, name, value, reference)
    {
        string? fragment = reference.Fragment is { Length: > 0 } raw ? UriReference.PercentDecode(raw) : null;
        AnchorName = fragment is { Length: > 0 } && fragment[0] != '/' ? fragment : null;
    }

    /// <summary>Gets the plain-name fragment, or <see langword="null"/> when the fragment is empty or a JSON Pointer.</summary>
    public string? AnchorName { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        SchemaNode target = GetTarget();
        if (AnchorName is string anchor
            && target.Resource.DynamicAnchors.TryGetValue(anchor, out SchemaNode? initial)
            && ReferenceEquals(initial, target))
        {
            target = context.FindDynamicAnchor(anchor) ?? target;
        }

        return Apply(context, target, instance);
    }
}
