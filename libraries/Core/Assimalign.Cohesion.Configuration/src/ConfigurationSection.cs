using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

/// <summary>
/// Represents a section of application configuration values.
/// </summary>
[DebuggerDisplay("{Key}: {Count}")]
public class ConfigurationSection : IConfigurationSection
{
    private readonly Key _key;
    private readonly KeyComparer _comparer = KeyComparer.Ordinal;
    private readonly List<IConfigurationEntry> _entries = new List<IConfigurationEntry>();

    #region Constructors

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public ConfigurationSection(Key key) 
    {
        _key = key;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="comparer"></param>
    public ConfigurationSection(Key key, KeyComparer comparer) : this(key)
    {
        _comparer = comparer;
    }

    #endregion

    #region Properties

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public string? this[in Path path]
    {
        get => GetConfigurationValue(path);
        set => SetConfigurationValue(path, value);
    }

    /// <summary>
    /// The entry key.
    /// </summary>
    public Key Key => _key;

    /// <summary>
    /// The total number entries in the section.
    /// </summary>
    public int Count => _entries.Count;

    #endregion

    #region Methods

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public IConfigurationEntry Get(Key key)
    {
        return default;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void Set(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry, nameof(entry));

        var existing = _entries.Find(p => _comparer.Equals(p.Key, entry.Key));

        if (existing is not null)
        {
            Remove(existing);
        }

        _entries.Add(entry);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    public void Remove(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry, nameof(entry));

        bool removed = _entries.Remove(entry);

        if (removed)
        {
            return;
        }
        
        IConfigurationEntry? existing = null;

        // If removal failed, try brute force. (This is mostly likely due to custom IConfigurationEntry type)
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_comparer.Equals((existing = _entries[i]).Key, entry.Key))
            {
                _entries.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public IConfigurationValue? GetValue(Key key)
    {
        return GetEntry<IConfigurationValue>(key);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IEnumerable<IConfigurationValue> GetValues()
    {
        return _entries.OfType<IConfigurationValue>();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public IConfigurationSection? GetSection(Key key)
    {
        return GetEntry<IConfigurationSection>(key);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IEnumerable<IConfigurationSection> GetSections()
    {
        return _entries.OfType<IConfigurationSection>();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        return _entries.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public IConfigurationChangeToken GetChangeToken()
    {
        throw new NotImplementedException();
    }

    private TEntry? GetEntry<TEntry>(in Key key) where TEntry : IConfigurationEntry
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i] is TEntry entry && _comparer.Equals(key, entry.Key))
            {
                return (TEntry)entry;
            }
        }

        return default;
    }
    private string? GetConfigurationValue(in Path path)
    {
        Key key = path[0];

        if (!path.IsComposite)
        {
            return GetValue(key)?.Value;
        }

        IConfiguration? configuration = this;
        IConfigurationValue? value = null;

        for (int i = 0; i < path.Count - 1; i++)
        {
            if (configuration is not null)
            {
                configuration = configuration.GetSection(path[i]);
            }
            else
            {
                break;
            }
        }

        if (configuration is not null)
        {
            value = configuration.GetValue(path[path.Count]);
        }

        return value?.Value;
    }

    private void SetConfigurationValue(in Path path, string? value)
    {
        Key key = path[0];

        if (path.IsEmpty)
        {
            ThrowHelper.ThrowInvalidOperationException("");
        }

        if (!path.IsComposite)
        {
            Set(new ConfigurationValue(key, value));
            return;
        }

        IConfigurationEntry? existing = GetEntry<IConfigurationEntry>(key);

        if (existing is null)
        {
            existing = new ConfigurationSection(key, _comparer);
        }
        // Check if changing from a leaf to a composite
        else if (existing is IConfigurationValue)
        {
            Remove(existing);
        }

        (existing as ConfigurationSection)![path.Subpath(1)] = value;

        Set(existing);
    }

    #endregion
}