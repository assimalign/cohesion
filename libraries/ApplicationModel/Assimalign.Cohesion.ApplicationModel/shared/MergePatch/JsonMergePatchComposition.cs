using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// Composes RFC 7396 merge patches, so one patch states the effect of applying two in order, and applies a patch to a
/// value. The model composes repeated <c>Configure</c> calls for a section this way, a gateway composes a management
/// PATCH into the stored override, and a platform surface applies its override over the value code declares.
/// </summary>
/// <remarks>
/// A null member is kept as a tombstone, so the composed patch still removes the member. Two sequences have no
/// single-patch form and resolve to the later patch: an object patch after a non-object patch, and an object member
/// after the removal of that member. In both, the later object merges into the target's value instead of replacing
/// it; a caller that needs an exact value states it with a replacing write.
/// </remarks>
// Deviates from the repo namespace-matches-assembly rule per design decision: this file is shared source
// (CohesionSharedSource), compiled into ApplicationModel and the gateway so both compose patches by one rule.
// It is a stateless static helper over System.Text.Json nodes, so linking a private copy into each is safe.
internal static class JsonMergePatchComposition
{
    /// <summary>Composes two merge patches, applying <paramref name="second"/> after <paramref name="first"/>.</summary>
    /// <param name="first">The earlier patch, or null for a JSON null.</param>
    /// <param name="second">The later patch, or null for a JSON null.</param>
    /// <returns>A new patch that neither argument shares nodes with.</returns>
    public static JsonNode? Compose(JsonNode? first, JsonNode? second)
    {
        if (second is not JsonObject incoming || first is not JsonObject existing)
        {
            return second?.DeepClone();
        }

        var composed = (JsonObject)existing.DeepClone();
        foreach (KeyValuePair<string, JsonNode?> member in incoming)
        {
            composed[member.Key] = member.Value is JsonObject && composed[member.Key] is JsonObject current
                ? Compose(current, member.Value)
                : member.Value?.DeepClone();
        }

        return composed;
    }

    /// <summary>Applies a merge patch to a value, by RFC 7396 section 2.</summary>
    /// <param name="target">The value, or null for a JSON null.</param>
    /// <param name="patch">The patch, or null for a JSON null.</param>
    /// <returns>A new value that neither argument shares nodes with; a null member of the patch removes the member.</returns>
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject members)
        {
            return patch?.DeepClone();
        }

        JsonObject result = target is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> member in members)
        {
            if (member.Value is null)
            {
                result.Remove(member.Key);
            }
            else
            {
                result[member.Key] = Apply(result[member.Key], member.Value);
            }
        }

        return result;
    }
}
