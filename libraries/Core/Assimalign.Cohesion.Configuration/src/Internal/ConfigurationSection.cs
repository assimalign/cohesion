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
    private readonly KeyComparison _comparison;
    private readonly Dictionary<Key, IConfigurationEntry> _data;
    private readonly Dictionary<Key, IConfigurationEntry>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    internal ConfigurationSection(Path path, string providerName)
        : this(path, providerName, KeyComparison.Ordinal)
    {
    }

    internal ConfigurationSection(
        Path path,
        string providerName,
        KeyComparison comparison,
        bool isReadOnly = false,
        ConfigurationSection? parent = null)
        : base(path, providerName, parent)
    {
        _comparison = ArgumentException.ThrowIfEnumNotDefined(comparison);
        _isReadOnly = isReadOnly;
        _data = new Dictionary<Key, IConfigurationEntry>(KeyComparer.FromComparison(_comparison));
        _lookup = _data.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <inheritdoc />
    public string? this[Path path]
    {
        get => GetConfigurationValue(Path.Combine(Path, path, _comparison));
        set => SetConfigurationValue(Path.Combine(Path, path, _comparison), value);
    }

    /// <inheritdoc />
    public int Count => _data.Count;

    /// <inheritdoc />
    public IConfigurationEntry? GetEntry(Path path)
    {
        return GetEntryCore(Path.Combine(Path, path, _comparison));
    }

    /// <inheritdoc />
    public IEnumerable<IConfigurationEntry> GetChildren()
    {
        return _data.Values;
    }

    private IConfigurationEntry? GetEntryCore(in Path path)
    {
        if (path.Count == Path.Count && path.Equals(Path, _comparison))
        {
            return this;
        }

        if (!TryGetNextKey(path, out Key key))
        {
            return null;
        }

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            return null;
        }

        if (entry is ConfigurationValue value)
        {
            return path.Count == Path.Count + 1
                ? value
                : null;
        }

        if (entry is ConfigurationSection section)
        {
            return path.Count > Path.Count + 1
                ? section.GetEntryCore(path)
                : section;
        }

        return null;
    }

    private string? GetConfigurationValue(in Path path)
    {
        if (!TryGetNextKey(path, out Key key))
        {
            return null;
        }

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            return null;
        }

        if (entry is ConfigurationValue value)
        {
            return path.Count == Path.Count + 1
                ? value.Value
                : null;
        }

        if (entry is ConfigurationSection section)
        {
            return path.Count > Path.Count + 1
                ? section.GetConfigurationValue(path)
                : null;
        }

        return null;
    }

    private void SetConfigurationValue(in Path path, string? input)
    {
        SetConfigurationValueCore(path, input, notifySelf: true, ignoreReadOnly: false);
    }

    internal bool SetValue(Path path, string? input, bool ignoreReadOnly = false)
    {
        return SetConfigurationValueCore(
            Path.Combine(Path, path, _comparison),
            input,
            notifySelf: true,
            ignoreReadOnly);
    }

    internal bool Remove(Path path)
    {
        return RemoveCore(Path.Combine(Path, path, _comparison));
    }

    private bool SetConfigurationValueCore(in Path path, string? input, bool notifySelf, bool ignoreReadOnly)
    {
        InvalidOperationException.ThrowIf(_isReadOnly && !ignoreReadOnly, "The configuration section is read-only.");

        if (!TryGetNextKey(path, out Key key))
        {
            throw new ArgumentException("The path must target a descendant of the current section.", nameof(path));
        }

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            AddEntry(path, key, input, notifySelf, ignoreReadOnly);
            return true;
        }

        if (entry is ConfigurationValue value)
        {
            if (path.Count == Path.Count + 1)
            {
                return value.SetValue(input, ignoreReadOnly);
            }

            ReplaceValueWithSection(path, key, value, input, notifySelf, ignoreReadOnly);
            return true;
        }

        if (entry is ConfigurationSection section)
        {
            if (path.Count == Path.Count + 1)
            {
                section.NotifyLocalChanged();
                _data[key] = new ConfigurationValue(path, input, ProviderName, _isReadOnly, this);

                if (notifySelf)
                {
                    NotifyChanged();
                }

                return true;
            }

            return section.SetConfigurationValueCore(path, input, notifySelf: true, ignoreReadOnly);
        }

        return false;
    }

    private bool RemoveCore(in Path path)
    {
        InvalidOperationException.ThrowIf(_isReadOnly, "The configuration section is read-only.");

        if (!TryGetNextKey(path, out Key key))
        {
            return false;
        }

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            return false;
        }

        if (path.Count == Path.Count + 1)
        {
            if (entry is ConfigurationEntry configurationEntry)
            {
                configurationEntry.NotifyLocalChanged();
            }

            if (!_data.Remove(key))
            {
                return false;
            }

            NotifyChanged();

            return true;
        }

        return entry is ConfigurationSection section && section.RemoveCore(path);
    }

    private void AddEntry(in Path path, in Key key, string? input, bool notifySelf, bool ignoreReadOnly)
    {
        if (path.Count > Path.Count + 1)
        {
            Path entryPath = path.Subpath(0, Path.Count + 1);
            var entry = new ConfigurationSection(entryPath, ProviderName, _comparison, _isReadOnly, this);

            // A brand-new child cannot have external subscribers yet, so we only notify once
            // after it is attached to the current section.
            entry.SetConfigurationValueCore(path, input, notifySelf: false, ignoreReadOnly);
            _data.Add(key, entry);
        }
        else
        {
            _data.Add(key, new ConfigurationValue(path, input, ProviderName, _isReadOnly, this));
        }

        if (notifySelf)
        {
            NotifyChanged();
        }
    }

    private void ReplaceValueWithSection(
        in Path path,
        in Key key,
        ConfigurationValue value,
        string? input,
        bool notifySelf,
        bool ignoreReadOnly)
    {
        Path entryPath = path.Subpath(0, Path.Count + 1);
        var entry = new ConfigurationSection(entryPath, ProviderName, _comparison, _isReadOnly, this);

        value.NotifyLocalChanged();
        entry.SetConfigurationValueCore(path, input, notifySelf: false, ignoreReadOnly);
        _data[key] = entry;

        if (notifySelf)
        {
            NotifyChanged();
        }
    }

    private bool TryGetNextKey(in Path path, out Key key)
    {
        if (path.Count <= Path.Count)
        {
            key = default;
            return false;
        }

        key = path[Path.Count];

        return true;
    }

    partial class DebuggerView
    {
        private readonly ConfigurationSection _section;

        public DebuggerView(ConfigurationSection section)
        {
            _section = section;
        }

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
    }
}
