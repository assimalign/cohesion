using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Configuration.Internal;

/// <summary>
/// Represents a section of application configuration values.
/// </summary>
[DebuggerDisplay("(S) {Key} = [{Count}]")]
[DebuggerTypeProxy(typeof(DebuggerView))]
internal class ConfigurationSection : ConfigurationEntry, IConfigurationSection
{
    private readonly bool _isReadOnly;
    private readonly KeyComparison _comparison = KeyComparison.Ordinal;
    private readonly Dictionary<Key, IConfigurationEntry> _data;
    private readonly Dictionary<Key, IConfigurationEntry>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    internal ConfigurationSection(Path path, string providerName) 
        : base(path, providerName)
    {
        _data = new Dictionary<Key, IConfigurationEntry>(KeyComparer.FromComparison(_comparison));
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

        Key key = path[Path.Count];

        if (!_lookup.TryGetValue(key, out var either))
        {
            return null;
        }
        else if (either.IsValue(out IConfigurationValue? value))
        {
            if (path.Count > Path.Count + 1)
            {
                return null;
            }
            return value;
        }
        else if (either.IsSection(out IConfigurationSection? section))
        {
            if (path.Count > Path.Count + 1)
            {
                return section.GetEntry(path);
            }
            return section;
        }

        return null;
    }

    /// <inheritdoc />
    public string ToValue()
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public IEnumerable<IConfigurationEntry> GetChildren()
    {
        return _data.Values;
    }

    private string? GetConfigurationValue(in Path path)
    {
        Key key = path[Path.Count];

        if (!_lookup.TryGetValue(key, out var either))
        {
            return null;
        }

        else if (either.IsValue(out IConfigurationValue? value))
        {
            if (path.Count > Path.Count + 1)
            {
                return null;
            }

            return value.Value;
        }
        else if (either.IsSection(out IConfigurationSection? section))
        {
            if (path.Count > Path.Count + 1)
            {
                var subEntry = section.GetEntry(path);
                return subEntry is IConfigurationValue subValue ? subValue.Value : null;
            }

            return null;
        }

        return null;
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
        else if (either.IsValue(out IConfigurationValue? value))
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
        else if (either.IsSection(out IConfigurationSection? section))
        {
            // Remove section and replace with value.
            if (path.Count < Path.Count + 1)
            {
                _data.Remove(key);
                _data.Add(key, new ConfigurationValue(path, input, ProviderName, _isReadOnly));
            }
            else
            {
                ((ConfigurationSection)section)[path] = input;
            }

            NotifyChanged();
        }
    }


    partial class DebuggerView
    {
        private readonly ConfigurationSection _section;
        public DebuggerView(ConfigurationSection section)
        {
            _section = section;
        }

        //public Key Key => _section.Key;
        //public Path Path => _section.Path;

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public IConfigurationEntry[] Entries
        {
            get
            {
                IConfigurationEntry[] entries = new IConfigurationEntry[_section.Count];

                int i = 0;
                foreach (var entry in _section.GetChildren())
                {
                    entries[i] = entry;
                    i++;
                }

                return entries;
            }
        }

        //public int? Count => _section.Value;
    }
}