using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;

/// <summary>
/// One <c>Set-Cookie</c> field as a client receives it, parsed here rather than with the Http.Cookies
/// model so the assertions do not depend on the code under test reading its own output.
/// </summary>
internal sealed class SetCookieField
{
    private readonly Dictionary<string, string?> _attributes;

    private SetCookieField(string raw, string name, string value, Dictionary<string, string?> attributes)
    {
        Raw = raw;
        Name = name;
        Value = value;
        _attributes = attributes;
    }

    /// <summary>Gets the field value exactly as received.</summary>
    public string Raw { get; }

    /// <summary>Gets the cookie name.</summary>
    public string Name { get; }

    /// <summary>Gets the cookie value.</summary>
    public string Value { get; }

    /// <summary>Gets the number of attributes the field carries.</summary>
    public int AttributeCount => _attributes.Count;

    /// <summary>Gets whether the field carries the attribute, matched case-insensitively.</summary>
    public bool Has(string attribute) => _attributes.ContainsKey(attribute);

    /// <summary>Gets the attribute's value, or <see langword="null"/> when it is absent or a flag.</summary>
    public string? Get(string attribute) => _attributes.TryGetValue(attribute, out string? value) ? value : null;

    /// <summary>Parses a <c>Set-Cookie</c> field value: <c>name=value</c>, then <c>;</c>-separated attributes.</summary>
    public static SetCookieField Parse(string raw)
    {
        string[] parts = raw.Split(';');
        string pair = parts[0].Trim();
        int equals = pair.IndexOf('=');
        string name = equals < 0 ? pair : pair[..equals];
        string value = equals < 0 ? string.Empty : pair[(equals + 1)..];

        Dictionary<string, string?> attributes = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0)
            {
                continue;
            }

            int attributeEquals = part.IndexOf('=');
            if (attributeEquals < 0)
            {
                attributes[part] = null;
            }
            else
            {
                attributes[part[..attributeEquals].Trim()] = part[(attributeEquals + 1)..].Trim();
            }
        }

        return new SetCookieField(raw, name, value, attributes);
    }

    /// <summary>Reads every <c>Set-Cookie</c> field of a response, one per field line.</summary>
    public static IReadOnlyList<SetCookieField> ReadAll(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.Select(Parse).ToArray()
            : [];

    /// <inheritdoc />
    public override string ToString() => Raw;
}
