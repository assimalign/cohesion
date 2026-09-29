using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// The mutable <see cref="ApplicationProviders.Sources"/> collection: an ordinal dictionary that
/// rejects keys a <c>&lt;source&gt;:&lt;key&gt;</c> mount source can never name and
/// <see langword="null"/> providers.
/// </summary>
internal sealed class ResourceSourceProviderDictionary : IDictionary<string, IResourceSourceProvider>
{
    private readonly Dictionary<string, IResourceSourceProvider> _providers = new(StringComparer.Ordinal);

    public IResourceSourceProvider this[string key]
    {
        get => _providers[key];
        set
        {
            ValidateKey(key);
            ArgumentNullException.ThrowIfNull(value);
            _providers[key] = value;
        }
    }

    public ICollection<string> Keys => _providers.Keys;

    public ICollection<IResourceSourceProvider> Values => _providers.Values;

    public int Count => _providers.Count;

    public bool IsReadOnly => false;

    public void Add(string key, IResourceSourceProvider value)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        _providers.Add(key, value);
    }

    public void Add(KeyValuePair<string, IResourceSourceProvider> item) => Add(item.Key, item.Value);

    public void Clear() => _providers.Clear();

    public bool Contains(KeyValuePair<string, IResourceSourceProvider> item) =>
        ((ICollection<KeyValuePair<string, IResourceSourceProvider>>)_providers).Contains(item);

    public bool ContainsKey(string key) => _providers.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, IResourceSourceProvider>[] array, int arrayIndex) =>
        ((ICollection<KeyValuePair<string, IResourceSourceProvider>>)_providers).CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<string, IResourceSourceProvider>> GetEnumerator() => _providers.GetEnumerator();

    public bool Remove(string key) => _providers.Remove(key);

    public bool Remove(KeyValuePair<string, IResourceSourceProvider> item) =>
        ((ICollection<KeyValuePair<string, IResourceSourceProvider>>)_providers).Remove(item);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out IResourceSourceProvider value) =>
        _providers.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.Contains(':'))
        {
            throw new ArgumentException(
                $"Mount source name '{key}' must not contain ':'. Register the <source> part of a " +
                "'<source>:<key>' mount source.",
                nameof(key));
        }

        if (string.Equals(key, ApplicationProviderValidation.ParameterSource, StringComparison.Ordinal) ||
            string.Equals(key, ApplicationProviderValidation.LiteralSource, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Mount source name '{key}' is reserved: the gateway resolves '{key}:' sources itself.",
                nameof(key));
        }
    }
}
