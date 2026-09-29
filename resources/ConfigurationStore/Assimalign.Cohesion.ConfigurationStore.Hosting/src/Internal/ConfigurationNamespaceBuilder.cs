using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.ConfigurationStore;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

internal sealed class ConfigurationNamespaceBuilder : IConfigurationNamespaceBuilder
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public IConfigurationNamespaceBuilder Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A configuration key cannot contain '/' because it separates the namespace from the command key.",
                nameof(key));
        }

        _values[key] = value;
        return this;
    }

    internal IReadOnlyDictionary<string, string?> Snapshot()
    {
        return new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(_values, StringComparer.Ordinal));
    }
}
