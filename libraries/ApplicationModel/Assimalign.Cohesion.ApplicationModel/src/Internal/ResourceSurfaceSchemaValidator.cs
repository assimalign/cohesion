using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// Validates JSON values and RFC 7396 merge patches against the JSON-schema subset that System.Text.Json's schema
/// exporter writes for a configuration section, an action's input or an action's result.
/// </summary>
/// <remarks>
/// <para>
/// Checked keywords: boolean schemas, <c>$ref</c> as a JSON pointer into the same schema, <c>anyOf</c> and
/// <c>oneOf</c> (both as "some branch matches"), <c>type</c>, <c>enum</c>, <c>const</c>, <c>pattern</c>,
/// <c>properties</c>, <c>additionalProperties</c>, <c>required</c> and <c>items</c>. Other keywords, such as
/// <c>format</c>, are not checked.
/// </para>
/// <para>
/// An object schema that lists <c>properties</c> and declares no <c>additionalProperties</c> is closed: a
/// source-generated options type binds only the members it declares and silently drops any other, so an unknown
/// member is reported instead of being accepted and ignored.
/// </para>
/// </remarks>
internal static class ResourceSurfaceSchemaValidator
{
    private const int maximumDepth = 64;
    private static readonly TimeSpan _patternTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Validates a complete value.</summary>
    /// <param name="schema">The schema of the value.</param>
    /// <param name="value">The value.</param>
    /// <returns>Null when the value conforms; otherwise why it does not.</returns>
    public static string? ValidateValue(JsonElement schema, JsonElement value) =>
        Check(schema, schema, value, "$", 0, patch: false);

    /// <summary>Validates an RFC 7396 merge patch for a value the schema describes.</summary>
    /// <param name="schema">The schema of the patched value.</param>
    /// <param name="patch">The merge patch.</param>
    /// <returns>Null when the patch conforms; otherwise why it does not.</returns>
    /// <remarks>
    /// A null patch resets the value. An object patch merges member by member, so its object members are checked as
    /// patches, required members may be absent, and a member set to null is a removal that must not name a required
    /// member. Any other patch replaces the value and is checked as a complete value.
    /// </remarks>
    public static string? ValidatePatch(JsonElement schema, JsonElement patch) =>
        patch.ValueKind == JsonValueKind.Null ? null : Check(schema, schema, patch, "$", 0, patch: true);

    private static string? Check(JsonElement root, JsonElement schema, JsonElement value, string path, int depth, bool patch)
    {
        if (depth > maximumDepth)
        {
            return Format(ErrorMessages.SurfaceValueTooDeep, path);
        }

        int hops = 0;
        while (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out JsonElement reference))
        {
            if (++hops > maximumDepth ||
                reference.ValueKind != JsonValueKind.String ||
                !TryResolvePointer(root, reference.GetString()!, out schema))
            {
                return Format(ErrorMessages.SurfaceReferenceInvalid, reference.ToString());
            }
        }

        switch (schema.ValueKind)
        {
            case JsonValueKind.True:
                return null;
            case JsonValueKind.False:
                return Format(ErrorMessages.SurfaceValueForbidden, path);
            case JsonValueKind.Object:
                break;
            default:
                return null;
        }

        // Only an object patch merges; any other patch value replaces and is checked whole.
        bool merges = patch && value.ValueKind == JsonValueKind.Object;
        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(type, value))
        {
            return Format(ErrorMessages.SurfaceValueTypeMismatch, path, DescribeType(type));
        }

        if (!merges && !MatchesValues(schema, value))
        {
            return Format(ErrorMessages.SurfaceValueNotAllowed, path);
        }

        if (value.ValueKind == JsonValueKind.String &&
            schema.TryGetProperty("pattern", out JsonElement pattern) &&
            pattern.ValueKind == JsonValueKind.String &&
            !MatchesPattern(pattern.GetString()!, value.GetString()!))
        {
            return Format(ErrorMessages.SurfacePatternMismatch, path);
        }

        if (CheckBranches(root, schema, value, path, depth, patch) is string branchError)
        {
            return branchError;
        }

        if (value.ValueKind == JsonValueKind.Object &&
            CheckMembers(root, schema, value, path, depth, merges) is string memberError)
        {
            return memberError;
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                string itemPath = path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                if (Check(root, items, item, itemPath, depth + 1, patch: false) is string itemError)
                {
                    return itemError;
                }

                index++;
            }
        }

        return null;
    }

    private static string? CheckBranches(JsonElement root, JsonElement schema, JsonElement value, string path, int depth, bool patch)
    {
        if ((!schema.TryGetProperty("anyOf", out JsonElement branches) && !schema.TryGetProperty("oneOf", out branches)) ||
            branches.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? first = null;
        foreach (JsonElement branch in branches.EnumerateArray())
        {
            string? error = Check(root, branch, value, path, depth + 1, patch);
            if (error is null)
            {
                return null;
            }

            first ??= error;
        }

        return first ?? Format(ErrorMessages.SurfaceValueNotAllowed, path);
    }

    private static string? CheckMembers(JsonElement root, JsonElement schema, JsonElement value, string path, int depth, bool merges)
    {
        bool hasProperties = schema.TryGetProperty("properties", out JsonElement properties) &&
            properties.ValueKind == JsonValueKind.Object;
        bool hasAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional);
        if (hasProperties || hasAdditional)
        {
            foreach (JsonProperty member in value.EnumerateObject())
            {
                string memberPath = MemberPath(path, member.Name);
                JsonElement memberSchema;
                if (hasProperties && properties.TryGetProperty(member.Name, out JsonElement declared))
                {
                    memberSchema = declared;
                }
                else if (hasAdditional && additional.ValueKind != JsonValueKind.False)
                {
                    memberSchema = additional;
                }
                else
                {
                    return Format(ErrorMessages.SurfaceMemberUnknown, memberPath);
                }

                if (merges && member.Value.ValueKind == JsonValueKind.Null)
                {
                    if (IsRequired(schema, member.Name))
                    {
                        return Format(ErrorMessages.SurfaceMemberNotRemovable, memberPath);
                    }

                    continue;
                }

                if (Check(root, memberSchema, member.Value, memberPath, depth + 1, merges) is string error)
                {
                    return error;
                }
            }
        }

        if (!merges && schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && !value.TryGetProperty(name.GetString()!, out _))
                {
                    return Format(ErrorMessages.SurfaceMemberRequired, path, name.GetString()!);
                }
            }
        }

        return null;
    }

    private static bool MatchesValues(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(constant, value))
        {
            return false;
        }

        if (!schema.TryGetProperty("enum", out JsonElement allowed) || allowed.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (JsonElement candidate in allowed.EnumerateArray())
        {
            if (JsonElement.DeepEquals(candidate, value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesType(JsonElement type, JsonElement value)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return MatchesType(type.GetString()!, value);
        }

        if (type.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (JsonElement candidate in type.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.String && MatchesType(candidate.GetString()!, value))
            {
                return true;
            }
        }

        return false;
    }

    // An integer is a number token with no fraction or exponent, which is what an integral member binds from.
    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.GetRawText().AsSpan().IndexOfAny(".eE") < 0,
        _ => true,
    };

    private static string DescribeType(JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.Array)
        {
            return "'" + type.ToString() + "'";
        }

        var description = new StringBuilder();
        foreach (JsonElement candidate in type.EnumerateArray())
        {
            if (description.Length > 0)
            {
                description.Append(" or ");
            }

            description.Append('\'').Append(candidate.ToString()).Append('\'');
        }

        return description.ToString();
    }

    private static bool MatchesPattern(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, _patternTimeout);
        }
        catch (ArgumentException)
        {
            // A pattern .NET cannot parse checks nothing, like any other keyword this validator does not know.
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsRequired(JsonElement schema, string member)
    {
        if (!schema.TryGetProperty("required", out JsonElement required) || required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement name in required.EnumerateArray())
        {
            if (name.ValueKind == JsonValueKind.String && name.ValueEquals(member))
            {
                return true;
            }
        }

        return false;
    }

    // A JSON pointer within the schema document: '#' or '#/segment/...', with '~1' for '/' and '~0' for '~'.
    private static bool TryResolvePointer(JsonElement root, string reference, out JsonElement target)
    {
        target = root;
        if (reference == "#")
        {
            return true;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string encoded in reference[2..].Split('/'))
        {
            string segment = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out JsonElement member))
            {
                target = member;
            }
            else if (target.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                index < target.GetArrayLength())
            {
                target = target[index];
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static string MemberPath(string path, string member)
    {
        foreach (char character in member)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return path + "['" + member.Replace("'", "\\'", StringComparison.Ordinal) + "']";
            }
        }

        return member.Length == 0 ? path + "['']" : path + "." + member;
    }

    private static string Format(string message, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, message, arguments);
}
