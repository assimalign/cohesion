using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web.Serialization;

namespace Assimalign.Cohesion.Web.Validation.Internal;

/// <summary>
/// Turns ObjectValidation errors into the <c>errors</c> extension member of a <c>400</c> problem: a map
/// from the failing member to its messages, the shape binding failures use
/// (<c>{ "Name": ["..."], "Address.City": ["..."] }</c>).
/// </summary>
internal static class ValidationErrorMap
{
    private const string arrow = "=>";

    /// <summary>
    /// Builds the map: one entry per key, in the order the rules ran, each holding its messages in that
    /// order.
    /// </summary>
    /// <param name="errors">The validation errors, in the order the rules reported them.</param>
    /// <param name="modelType">The declared model type for JSON contract lookup.</param>
    /// <param name="serialization">The application's content-serialization registry, when present.</param>
    /// <returns>The map, with <see cref="string"/> array values the problem-details writer serializes.</returns>
    public static Dictionary<string, object?> Create(
        IEnumerable<IValidationError> errors,
        Type? modelType = null,
        IHttpContentSerializationFeature? serialization = null)
    {
        Dictionary<string, List<string>> messages = new(StringComparer.Ordinal);
        List<string> keys = new();

        // ObjectValidation lists errors in the order its rules ran, which is the order the profile declares
        // its members and chains their rules, so a key's first error places it.
        foreach (IValidationError error in errors)
        {
            string key = GetKey(error.Source);
            if (modelType is not null && serialization is not null
                && !string.Equals(key, error.Source, StringComparison.Ordinal) && HasValidIndexes(key))
            {
                key = GetJsonPath(key, modelType, serialization);
            }

            if (!messages.TryGetValue(key, out List<string>? list))
            {
                list = new List<string>();
                messages.Add(key, list);
                keys.Add(key);
            }

            list.Add(error.Message ?? string.Empty);
        }

        Dictionary<string, object?> map = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            map.Add(key, messages[key].ToArray());
        }

        return map;
    }

    private static bool HasValidIndexes(string path)
    {
        foreach (string segment in path.Split('.'))
        {
            int index = segment.IndexOf('[');
            if (index < 0)
            {
                if (segment.Length == 0 || segment.Contains(']'))
                {
                    return false;
                }
                continue;
            }
            if (index == 0)
            {
                return false;
            }
            while (index < segment.Length)
            {
                if (segment[index++] != '[')
                {
                    return false;
                }
                int digits = index;
                while (index < segment.Length && segment[index] is >= '0' and <= '9')
                {
                    index++;
                }
                if (index == digits || index == segment.Length || segment[index++] != ']')
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static string GetJsonPath(string path, Type modelType, IHttpContentSerializationFeature serialization)
    {
        Type? owner = modelType;
        StringBuilder mapped = new();
        bool first = true;
        foreach (string segment in path.Split('.'))
        {
            if (!first)
            {
                mapped.Append('.');
            }
            first = false;

            int bracket = segment.IndexOf('[');
            string member = bracket < 0 ? segment : segment.Substring(0, bracket);
            JsonPropertyInfo? property = null;
            if (owner is not null && serialization.TryGetJsonTypeInfo(owner, out JsonTypeInfo? contract)
                && contract.Kind == JsonTypeInfoKind.Object)
            {
                property = GetProperty(contract, member);
            }

            mapped.Append(property?.Name ?? member);
            owner = property?.PropertyType;
            if (bracket >= 0)
            {
                mapped.Append(segment.AsSpan(bracket));
                // Each bracket consumes one collection level, including nested collections.
                for (int i = bracket; i < segment.Length; i++)
                {
                    if (segment[i] == '[')
                    {
                        owner = owner is not null && serialization.TryGetJsonTypeInfo(owner, out JsonTypeInfo? collection)
                            ? collection.ElementType
                            : null;
                    }
                }
            }
        }

        return mapped.ToString();
    }

    private static JsonPropertyInfo? GetProperty(JsonTypeInfo contract, string memberName)
    {
        // A customized resolver can identify properties after arbitrary contract modifications. Generated
        // contexts supply the CLR/attribute-name identity that JsonPropertyInfo otherwise keeps internal.
        if (contract.Options.TypeInfoResolver is IValidationJsonPropertyResolver optionsResolver
            && optionsResolver.GetProperty(contract, memberName) is { } optionsProperty)
        {
            return optionsProperty;
        }

        if (contract.OriginatingResolver is IValidationJsonPropertyResolver originatingResolver
            && originatingResolver.GetProperty(contract, memberName) is { } originatingProperty)
        {
            return originatingProperty;
        }

        // A context built without the Web generator can still describe ordinary policy-named properties
        // and constructor-bound renamed properties. Never read AttributeProvider: it resolves by reflection.
        string candidate = contract.Options.PropertyNamingPolicy?.ConvertName(memberName) ?? memberName;
        foreach (JsonPropertyInfo property in contract.Properties)
        {
            if (string.Equals(property.AssociatedParameter?.Name, memberName, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        foreach (JsonPropertyInfo property in contract.Properties)
        {
            if (string.Equals(property.Name, candidate, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the key an error is reported under. A rule's default source is its member selector's text,
    /// <c>p =&gt; p.Address.City</c>, which is reported as the member path a client names,
    /// <c>Address.City</c>. A source the profile set explicitly is reported as written, and an error with
    /// no source — one about the value as a whole — under the empty key.
    /// </summary>
    /// <param name="source">The error's source.</param>
    /// <returns>The key.</returns>
    public static string GetKey(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        int separator = source.IndexOf(arrow, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return source;
        }

        string parameter = source.Substring(0, separator).Trim();
        string body = source.Substring(separator + arrow.Length).Trim();

        if (parameter.Length > 0
            && body.Length > parameter.Length + 1
            && body.StartsWith(parameter, StringComparison.Ordinal)
            && body[parameter.Length] == '.')
        {
            return body.Substring(parameter.Length + 1);
        }

        return source;
    }
}
