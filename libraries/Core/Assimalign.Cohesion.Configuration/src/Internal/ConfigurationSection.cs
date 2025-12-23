using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Configuration.Internal;



/// <summary>
/// Represents a section of application configuration values.
/// </summary>
[DebuggerDisplay("{Key} = {Count}")]
internal class ConfigurationSection : ConfigurationEntry, IConfigurationSection
{
    private readonly bool _isReadOnly;
    private readonly KeyComparison _comparison = KeyComparison.Ordinal;
    private readonly Dictionary<Key, Either<IConfigurationValue, IConfigurationSection>> _data;
    private readonly Dictionary<Key, Either<IConfigurationValue, IConfigurationSection>>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    internal ConfigurationSection(Path path, string providerName) 
        : base(path, providerName)
    {
        _data = new Dictionary<Key, Either<IConfigurationValue, IConfigurationSection>>(KeyComparer.FromComparison(_comparison));
        _lookup = _data.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    internal ConfigurationSection(Path path, string providerName, KeyComparison comparison, bool isReadOnly = false) 
        : this(path, providerName)
    {
        _comparison = ArgumentException.ThrowIfEnumNotDefined(comparison);
        _isReadOnly = isReadOnly;
    }

    /// <inheritdoc />
    public string? this[Path path]
    {
        get => GetConfigurationValue(Path.Combine(path, _comparison));
        set => SetConfigurationValue(Path.Combine(path, _comparison), value);
    }

    /// <inheritdoc />
    public int Count => _data.Count;

    /// <inheritdoc />
    public IConfigurationEntry? GetEntry(Path path)
    {
        path = Path.Combine(path, _comparison);

        Key key = path[0];

        if (!_lookup.TryGetValue(key, out var either))
        {
            return null;
        }

        if (either.If(out IConfigurationValue value, out IConfigurationSection section))
        {
            if (path.Count > 1)
            {
                return null;
            }
            return value;
        }
        else
        {
            if (path.Count > 1)
            {
                return section.GetEntry(path.Subpath(1));
            }
            return null;
        }
    }

    /// <inheritdoc />
    public string ToValue()
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        return _data.Values.Select(either =>
        {
            return either.If(out IConfigurationSection section, out IConfigurationValue value) ?
                (IConfigurationEntry)section :
                value;

        }).GetEnumerator();
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private string? GetConfigurationValue(in Path path)
    {
        Key key = path[Path.Count];

        if (!_lookup.TryGetValue(key, out var either))
        {
            return null;
        }

        if (either.If(out IConfigurationValue value, out IConfigurationSection section))
        {
            if (path.IsComposite)
            {
                return null;
            }

            return value.Value;
        }
        else
        {
            if (path.IsComposite)
            {
                return section.GetValue(path.Subpath(1))?.Value;
            }

            return null;
        }
    }
    private void SetConfigurationValue(in Path path, string? input)
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException("The configuration section is read-only.");
        }

        Key key = path[Path.Count];

        // If No Entry
        if (!_lookup.TryGetValue(key, out var either))
        {
            if (path.Count > Path.Count + 1)
            {
                var entryPath = path.Subpath(0, Path.Count + 1);
                var entry = new ConfigurationSection(entryPath, ProviderName, _comparison, _isReadOnly);

                entry[path] = input;

                _data.Add(key, entry);
            }
            else
            {
                _data.Add(key, new ConfigurationValue(path, input, ProviderName, _isReadOnly));
            }

            NotifyChanged();
        }
        else if (either.If(out IConfigurationValue value))
        {
            if (path.Count == Path.Count + 1)
            {
                value.Value = input;
            }
            else
            {
                var entryPath = path.Subpath(0, Path.Count + 1);
                var entry = new ConfigurationSection(entryPath, ProviderName, _comparison, _isReadOnly);
                
                entry[path] = input;

                // Remove the value and replace with a section.
                _data.Remove(key, out var old);
                _data.Add(key, entry);
            }

            NotifyChanged();
        }
        else if (either.If(out IConfigurationSection section))
        {
            // Remove section and replace with value.
            if (path.Count < Path.Count + 1)
            {
                _data.Remove(key);
                _data.Add(key, new ConfigurationValue(path, input, ProviderName, _isReadOnly));
            }
            else
            {
                section[path] = input;
            }

            NotifyChanged();
        }
    }
}