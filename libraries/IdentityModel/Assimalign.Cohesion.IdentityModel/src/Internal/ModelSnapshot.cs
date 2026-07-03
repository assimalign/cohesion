using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Shared snapshot logic for materializing immutable models from mutable descriptors.
/// </summary>
internal static class ModelSnapshot
{
    internal static readonly IReadOnlyDictionary<string, IdentityClaimValue> EmptyProperties =
        ReadOnlyDictionary<string, IdentityClaimValue>.Empty;

    internal static IReadOnlyList<string> Strings(IList<string> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);

        if (source.Count == 0)
        {
            return Array.Empty<string>();
        }

        var snapshot = new string[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source[index], parameterName);
            snapshot[index] = source[index];
        }

        return new ReadOnlyCollection<string>(snapshot);
    }

    internal static IReadOnlyDictionary<string, IdentityClaimValue> Properties(
        IDictionary<string, IdentityClaimValue> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        return Properties(source, source.Count, parameterName);
    }

    internal static IReadOnlyDictionary<string, IdentityClaimValue> Properties(
        IReadOnlyDictionary<string, IdentityClaimValue>? source,
        string parameterName)
    {
        if (source is null or { Count: 0 })
        {
            return EmptyProperties;
        }

        return Properties(source, source.Count, parameterName);
    }

    private static IReadOnlyDictionary<string, IdentityClaimValue> Properties(
        IEnumerable<KeyValuePair<string, IdentityClaimValue>> source,
        int count,
        string parameterName)
    {
        if (count == 0)
        {
            return EmptyProperties;
        }

        var snapshot = new Dictionary<string, IdentityClaimValue>(count, StringComparer.Ordinal);
        foreach (var (name, value) in source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);

            if (value.IsUndefined)
            {
                throw new ArgumentException(
                    $"The property '{name}' must not have an undefined value.",
                    parameterName);
            }

            snapshot.Add(name, value);
        }

        return new ReadOnlyDictionary<string, IdentityClaimValue>(snapshot);
    }
}
