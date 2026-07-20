using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Caching.Internal;

/// <summary>
/// Builds output-cache keys. The <em>primary</em> key is derived from the request target and the policy's
/// <c>VaryBy*</c> rules and is known before the endpoint runs. The <em>secondary</em> (variant) key is
/// derived from the stored response's own <c>Vary</c> header applied to the current request (RFC 9111
/// §4.1), so a representation that varies by <c>Accept-Encoding</c> or <c>Accept</c> keys on the client's
/// value of those headers — a client that cannot accept a stored variant computes a different key and
/// never receives it.
/// </summary>
/// <remarks>
/// Keys are plain delimited strings using the ASCII unit separator (<c>0x1F</c>) — a byte that cannot
/// appear in a header name, a path, or a query token — so component boundaries are unambiguous without
/// hashing. A distributed store adapter that prefers fixed-length keys may hash the string itself.
/// </remarks>
internal static class OutputCacheKeyBuilder
{
    // ASCII unit separator: cannot appear in a header name, path, or query token.
    private const char Separator = '\u001F';

    // ASCII record separator: fences the variant suffix from the primary key.
    private const string VariantSeparator = "\u001E";

    public static string BuildPrimaryKey(IHttpContext context, OutputCachePolicy policy, RouteValueDictionary? routeValues)
    {
        IHttpRequest request = context.Request;

        StringBuilder builder = new();
        builder.Append("oc").Append(Separator);
        builder.Append(request.Method.Value).Append(Separator);
        builder.Append((int)request.Scheme).Append(Separator);
        builder.Append(request.Host.Value).Append(Separator);
        builder.Append(request.Path.Value);

        AppendQuery(builder, request, policy);
        AppendVaryByHeaders(builder, request, policy);
        AppendVaryByRouteValues(builder, routeValues, policy);

        return builder.ToString();
    }

    public static string BuildVariantKey(string primaryKey, IHttpRequest request, IReadOnlyList<string> varyBy)
    {
        // Sort the field-names so the variant suffix is order-independent (Vary token order carries no
        // meaning).
        string[] names = new string[varyBy.Count];
        for (int i = 0; i < varyBy.Count; i++)
        {
            names[i] = varyBy[i];
        }
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);

        StringBuilder builder = new(primaryKey);
        builder.Append(VariantSeparator);
        for (int i = 0; i < names.Length; i++)
        {
            builder.Append(names[i]).Append('=');
            if (request.Headers.TryGetValue(new HttpHeaderKey(names[i]), out HttpHeaderValue value))
            {
                builder.Append(value.Value);
            }
            builder.Append(Separator);
        }

        return builder.ToString();
    }

    private static void AppendQuery(StringBuilder builder, IHttpRequest request, OutputCachePolicy policy)
    {
        builder.Append(Separator).Append('q');

        if (policy.VaryByQueryKeys.Count == 0)
        {
            // Fold the whole query string, sorted so ordering differences do not fragment the cache.
            List<KeyValuePair<string, string>> entries = new();
            foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> pair in request.Query)
            {
                entries.Add(new KeyValuePair<string, string>(pair.Key.ToString(), pair.Value.ToString()));
            }

            entries.Sort(static (a, b) =>
            {
                int byKey = string.CompareOrdinal(a.Key, b.Key);
                return byKey != 0 ? byKey : string.CompareOrdinal(a.Value, b.Value);
            });

            foreach (KeyValuePair<string, string> entry in entries)
            {
                builder.Append(Separator).Append(entry.Key).Append('=').Append(entry.Value);
            }

            return;
        }

        // Only the listed query keys participate; sort them for a stable key.
        string[] keys = new string[policy.VaryByQueryKeys.Count];
        policy.VaryByQueryKeys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);

        foreach (string key in keys)
        {
            builder.Append(Separator).Append(key).Append('=');
            if (request.Query.TryGetValue(new HttpQueryKey(key), out HttpQueryValue value))
            {
                builder.Append(value.ToString());
            }
        }
    }

    private static void AppendVaryByHeaders(StringBuilder builder, IHttpRequest request, OutputCachePolicy policy)
    {
        if (policy.VaryByHeaders.Count == 0)
        {
            return;
        }

        builder.Append(Separator).Append('h');

        string[] names = new string[policy.VaryByHeaders.Count];
        policy.VaryByHeaders.CopyTo(names, 0);
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            builder.Append(Separator).Append(name).Append('=');
            if (request.Headers.TryGetValue(new HttpHeaderKey(name), out HttpHeaderValue value))
            {
                builder.Append(value.Value);
            }
        }
    }

    private static void AppendVaryByRouteValues(StringBuilder builder, RouteValueDictionary? values, OutputCachePolicy policy)
    {
        if (policy.VaryByRouteValues.Count == 0)
        {
            return;
        }

        builder.Append(Separator).Append('r');

        string[] names = new string[policy.VaryByRouteValues.Count];
        policy.VaryByRouteValues.CopyTo(names, 0);
        Array.Sort(names, StringComparer.Ordinal);

        foreach (string name in names)
        {
            builder.Append(Separator).Append(name).Append('=');
            if (values is not null && values.TryGetValue(name, out object? value) && value is not null)
            {
                builder.Append(value.ToString());
            }
        }
    }
}
