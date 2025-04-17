using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("Configuration Provider: {Name}")]
public abstract class ConfigurationProvider : IConfigurationProvider
{
    private readonly List<IConfigurationEntry> _entries;
    private readonly KeyComparer _comparer;

    private bool _isDisposed;
    private bool _isLoaded;

    #region Constructors

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
        _comparer = ThrowHelper.ThrowIfNull(comparer);
        _entries = new List<IConfigurationEntry>();
    }

    #endregion

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
            ThrowHelper.ThrowArgumentException("'key' cannot be empty.");
        }

        return _entries.FirstOrDefault(p => _comparer.Equals(p.Key, key));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public virtual void Set(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry, nameof(entry));

        if (entry.Key.IsEmpty)
        {
            ThrowHelper.ThrowArgumentException("'IConfigurationEntry.Key' cannot be empty.");
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            if (_comparer.Equals(_entries[i].Key, entry.Key))
            {
                _entries.RemoveAt(i);
            }
        }

        _entries.Add(entry);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    public virtual void Remove(IConfigurationEntry entry)
    {
        bool removed = _entries.Remove(entry);

        if (!removed)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_comparer.Equals(_entries[i].Key, entry.Key))
                {
                    _entries.RemoveAt(i);
                }
            }
        }
    }

    public abstract Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public virtual IEnumerable<IConfigurationEntry> GetEntries()
    {
        return _entries;
    }

    public virtual void Load() => ReloadAsync().GetAwaiter().GetResult();

    public virtual async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<Path, string?>();

        await OnLoadAsync(entries, cancellationToken);

        foreach (var (path, value) in entries)
        {
            Key key = path[0];

            if (path.IsComposite)
            {
                var existing = Get(key) as ConfigurationSection;

                if (existing is null)
                {
                    existing = new ConfigurationSection(key, _comparer);
                }

                Path subpath = path.Subpath(1);

                existing[subpath] = value;

                Set(existing);
            }
            else
            {
                Set(new ConfigurationValue(key, value));
            }
        }
    }

    public void Reload()
    {
        ReloadAsync().GetAwaiter().GetResult();
    }

    public virtual Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _entries.Clear();
        return LoadAsync(cancellationToken);
    }

    public virtual void Dispose()
    {
        _entries.Clear();
    }

    public virtual ValueTask DisposeAsync()
    {
        _entries.Clear();

        return ValueTask.CompletedTask;
    }

    public bool ContainsKey(Key key)
    {
        return _entries.Any(p => _comparer.Equals(p.Key, key));
    }
}