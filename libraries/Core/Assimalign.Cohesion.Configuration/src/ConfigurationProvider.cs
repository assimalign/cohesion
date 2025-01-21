using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("Configuration Provider: {Name}")]
public abstract class ConfigurationProvider : IConfigurationProvider
{
    private readonly List<IConfigurationEntry> entries;
    private readonly KeyComparer comparer;

    /// <summary>
    /// 
    /// </summary>
    protected ConfigurationProvider() : this(KeyComparer.Ordinal)
    {
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="comparer"></param>
    protected ConfigurationProvider(KeyComparer comparer)
    {
        this.comparer = comparer;
        this.entries = new List<IConfigurationEntry>();
    }

    /// <summary>
    /// The provider name if any.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception> 
    public virtual IConfigurationEntry? Get(Key key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException();
        }

        return entries.FirstOrDefault(p => comparer.Equals(p.Key, key));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public virtual void Set(IConfigurationEntry? entry)
    {
        if (entry is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(entry));
        }

        if (entry.Key.IsEmpty)
        {
            ThrowHelper.ThrowArgumentException("The entry key cannot be empty.");
        }

        var existing = Get(entry.Key);

        if (existing is not null)
        {
            bool removed = entries.Remove(entry);

            // If removal failed, try brute force
            if (!removed)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var item = entries[i];

                    if (comparer.Equals(item.Key, entry.Key))
                    {
                        entries.RemoveAt(i);
                    }
                }
            }

            entries.Add(entry);
        }
        else
        {
            entries.Add(entry);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public virtual IEnumerable<IConfigurationEntry> GetEntries()
    {
        return entries;
    }

    public virtual void Load()
    {
        ReloadAsync().GetAwaiter().GetResult();
    }

    public abstract Task LoadAsync(CancellationToken cancellationToken = default);

    public void Reload()
    {
        ReloadAsync().GetAwaiter().GetResult();
    }

    public virtual Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        entries.Clear();
        return LoadAsync(cancellationToken);
    }

    public virtual void Dispose()
    {
        entries.Clear();
    }

    public virtual ValueTask DisposeAsync()
    {
        entries.Clear();

        return ValueTask.CompletedTask;
    }

    public bool ContainsKey(Key key)
    {
        return entries.Any(p => comparer.Equals(p.Key, key));
    }
}
