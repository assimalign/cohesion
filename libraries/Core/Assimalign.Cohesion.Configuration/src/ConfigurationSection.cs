using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;


/// <summary>
/// Represents a section of application configuration values.
/// </summary>
[DebuggerDisplay("{Key}: {Count}")]
public class ConfigurationSection : IConfigurationSection
{
    private readonly List<IConfigurationEntry> entries = new();

    #region Constructors

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public ConfigurationSection(Key key) 
    {
        Key = key;
    }

    #endregion

    #region Properties

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public string? this[KeyPath path]
    {
        get => GetConfigurationValue(path);
        set => throw new NotImplementedException();
    }

    /// <summary>
    /// The entry key.
    /// </summary>
    public Key Key { get; }

    /// <summary>
    /// The total number entries in the section.
    /// </summary>
    public int Count => entries.Count;

    #endregion


    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void Add(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry, nameof(entry));

        var existing = entries.Find(p => p.Key == entry.Key);

        if (existing is not null)
        {
            Remove(existing);
        }

        entries.Add(entry);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    public void Remove(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry, nameof(entry));

        bool removed = entries.Remove(entry);

        if (removed)
        {
            return;
        }
        
        IConfigurationEntry? existing = null;

        // If removal failed, try brute force. (This is mostly likely due to custom IConfigurationEntry type)
        for (int i = 0; i < entries.Count; i++)
        {
            if ((existing = entries[i]).Key == entry.Key)
            {
                entries.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public IConfigurationValue? GetValue(Key key) => GetEntry<IConfigurationValue>(key);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IEnumerable<IConfigurationValue> GetValues() => entries.OfType<IConfigurationValue>();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public IConfigurationSection? GetSection(Key key) => GetEntry<IConfigurationSection>(key);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IEnumerable<IConfigurationSection> GetSections() => entries.OfType<IConfigurationSection>();

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IEnumerator<IConfigurationEntry> GetEnumerator() => entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IConfigurationChangeToken GetChangeToken()
    {
        throw new NotImplementedException();
    }

    private TEntry? GetEntry<TEntry>(Key key) where TEntry : IConfigurationEntry
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] is TEntry entry && entry.Key == key)
            {
                return (TEntry)entry;
            }
        }

        return default;
    }


    private string? GetConfigurationValue(KeyPath path)
    {
        if (path.IsComposite)
        {
            return GetValue(path[0])?.Value;
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
}