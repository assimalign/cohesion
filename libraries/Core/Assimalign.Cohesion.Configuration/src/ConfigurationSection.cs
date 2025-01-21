using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

/// <summary>
/// Represents a section of application configuration values.
/// </summary>
[DebuggerDisplay("{Key}: Count - {Count}")]
public class ConfigurationSection : IConfigurationSection
{
    private readonly List<IConfigurationEntry> entries = new();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    public ConfigurationSection(Key key)
    {
        Key = key;
    }

    /// <summary>
    /// 
    /// </summary>
    public Key Key { get; }

    /// <summary>
    /// 
    /// </summary>
    public int Count => entries.Count;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public IConfigurationEntry this[Key key] 
    { 
        get => throw new NotImplementedException(); 
        set => throw new NotImplementedException(); 
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public object? this[KeyPath path]
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void Add(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry, nameof(entry));

        if (entry is IConfigurationSection section)
        {
            bool existing = false;

            for (int i = 0; i < entries.Count; i++)
            {
                var item = entries[i];

                if (!(existing = item.Key == entry.Key))
                {
                    continue;
                }
                // Switch to composite structure
                if (item is IConfigurationValue)
                {
                    entries.Remove(item);
                    entries.Add(entry);
                }
                if (item is IConfigurationSection)
                {
                    // Copy items to existing 
                    foreach (var child in section)
                    {
                        ((IConfigurationSection)item).Add(child);
                    }
                }
                break;
            }
            // if no existing value, then simply add
            if (!existing)
            {
                entries.Add(entry);
            }
        }
        else if (entry is IConfigurationValue value)
        {
            // Just add or override what is existing
            entries.Add(value);
        }
        else
        {
            // Invalid entry
            throw new Exception();
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    public void Remove(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry, nameof(entry));

        bool removed = entries.Remove(entry);

        // If removal failed, try brute force. (This is mostly likely due to custom IConfigurationEntry type)
        if (!removed)
        {
            // TODO
        }
    }

    public bool ContainsKey(Key key)
    {
        throw new NotImplementedException();
    }

    public IConfigurationValue GetValue(Key key)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IConfigurationValue> GetValues()
    {
        return entries.OfType<IConfigurationValue>();
    }

    public IConfigurationSection GetSection(Key key)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IConfigurationSection> GetSections()
    {
        return entries.OfType<IConfigurationSection>();
    }

    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        return entries.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public IConfigurationChangeToken GetChangeToken()
    {
        throw new NotImplementedException();
    }
}