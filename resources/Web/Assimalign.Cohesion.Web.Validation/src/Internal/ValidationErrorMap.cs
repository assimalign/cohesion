using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.ObjectValidation;

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
    /// <param name="errors">The validation errors.</param>
    /// <returns>The map, with <see cref="string"/> array values the problem-details writer serializes.</returns>
    public static Dictionary<string, object?> Create(IEnumerable<IValidationError> errors)
    {
        Dictionary<string, List<string>> messages = new(StringComparer.Ordinal);
        List<string> keys = new();

        // ObjectValidation records errors on a stack, so they enumerate newest first: reverse them to
        // report in the order the rules ran.
        foreach (IValidationError error in errors.Reverse())
        {
            string key = GetKey(error.Source);

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
