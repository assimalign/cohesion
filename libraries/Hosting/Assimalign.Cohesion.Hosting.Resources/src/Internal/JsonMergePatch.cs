using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Applies an RFC 7396 merge patch: objects merge member by member, a <see langword="null"/> member removes
/// the member, and every other value, arrays included, replaces the target. Neither input is mutated.
/// </summary>
internal static class JsonMergePatch
{
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        JsonObject result = target is JsonObject targetObject
            ? targetObject.DeepClone().AsObject()
            : new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> member in patchObject)
        {
            if (member.Value is null)
            {
                result.Remove(member.Key);
                continue;
            }

            JsonNode? current = result.TryGetPropertyValue(member.Key, out JsonNode? existing) ? existing : null;
            result[member.Key] = Apply(current, member.Value);
        }

        return result;
    }
}
