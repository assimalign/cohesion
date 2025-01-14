using System;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public abstract class ConfigurationProvider : IConfigurationProvider
{
    protected ConfigurationProvider()
    {
        Data = new List<IConfigurationEntry>();
    }

    protected virtual KeyComparer Comparer { get; } = KeyComparer.Ordinal;
    protected virtual List<IConfigurationEntry> Data { get; }
    public abstract string Name { get; }
    public virtual IConfigurationEntry? Get(Key key)
    {
        for (int i = 0; i < Data.Count; i++)
        {
            var entry = Data[i];

            if (Comparer.Equals(key, entry.Key))
            {
                return entry;
            }
        }
        return default;
    }
    public virtual void Set(IConfigurationEntry? entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        if (entry.Key.IsEmpty)
        {
            throw new ArgumentException("The entry key cannot be empty.");
        }

        IConfigurationEntry? existing = default;

        if ((existing = Get(entry.Key)) is not null)
        {
            Data.Remove(existing);
            Data.Add(entry);
        }
        else
        {
            Data.Add(entry);
        }
    }
    public virtual IEnumerable<IConfigurationEntry> GetEntries()
    {
        return Data;
    }
    public virtual void Load()
    {
        RefreshAsync().GetAwaiter().GetResult();
    }
    public abstract Task LoadAsync();
    public void Refresh()
    {
        RefreshAsync().GetAwaiter().GetResult();
    }
    public virtual Task RefreshAsync()
    {
        Data.Clear();

        return RefreshAsync();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
