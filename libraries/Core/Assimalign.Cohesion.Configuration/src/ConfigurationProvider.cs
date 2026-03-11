using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Configuration;

using Internal;

[DebuggerDisplay("Provider: {Name} = {_data.Count}")]
[DebuggerTypeProxy(typeof(DebuggerView))]
public abstract class ConfigurationProvider : IConfigurationProvider
{
    private readonly KeyComparison _comparison;
    private readonly ConcurrentDictionary<Key, IConfigurationEntry> _data;
    private readonly ConcurrentDictionary<Key, IConfigurationEntry>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly bool _isReadOnly;

    private bool _isLoading;
    private bool _isDisposed;

    protected ConfigurationProvider() : this(KeyComparison.OrdinalIgnoreCase, Timeout.InfiniteTimeSpan)
    {
    }

    protected ConfigurationProvider(KeyComparison comparison) : this(comparison, Timeout.InfiniteTimeSpan)
    {
    }

    protected ConfigurationProvider(TimeSpan timeout) : this(KeyComparison.OrdinalIgnoreCase, timeout)
    {
    }

    protected ConfigurationProvider(KeyComparison comparison, TimeSpan timeout, bool isReadOnly = false)
    {
        _comparison = comparison;
        _data = new ConcurrentDictionary<Key, IConfigurationEntry>(KeyComparer.FromComparison(comparison));
        _lookup = _data.GetAlternateLookup<ReadOnlySpan<char>>();
        _cancellationTokenSource = new CancellationTokenSource(timeout);
        _isReadOnly = isReadOnly;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public bool Exists(Path path)
    {
        return GetEntry(path) is not null;
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException"></exception>
    public bool TryGet(Path path, [NotNullWhen(true)] out string? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        value = null;

        if (_isLoading)
        {
            return false;
        }

        Key key = path[0];

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            return false;
        }
        else if (entry.IsValue(out IConfigurationValue? val))
        {
            // If result is a value, but the key is composite then return false
            if (path.IsComposite)
            {
                return false;
            }

            value = val.Value!;
        }
        else if (entry.IsSection(out IConfigurationSection? section))
        {
            if (path.IsComposite)
            {
                value = section.GetValue(path.Subpath(1))?.Value!;
            }
        }

        return value is not null;
    }

    /// <inheritdoc />
    public bool TrySet(Path path, string? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Only allow value sets on readonly and internal reloads
        if (_isReadOnly && !_isLoading)
        {
            return false;
        }

        Key key = path[0];

        // If No Entry
        if (!_lookup.TryGetValue(key, out IConfigurationEntry? item))
        {
            if (path.Count > 1)
            {
                Path entryPath = path.Subpath(0, 1);
                ConfigurationSection entry = new ConfigurationSection(entryPath, Name, _comparison, _isReadOnly);

                entry[path] = value;

                return _data.TryAdd(key, entry);
            }
            else
            {
                return _data.TryAdd(key, new ConfigurationValue(path, value, Name, _isReadOnly));
            }
        }
        if (item.IsValue(out IConfigurationValue? val))
        {
            if (path.Count == 1)
            {
                val.Value = value;
            }
            else
            {
                Path entryPath = path.Subpath(0, 1);
                ConfigurationSection entry = new ConfigurationSection(entryPath, Name, _comparison, _isReadOnly);

                entry[path] = value;

                // Remove the value and replace with a section.
                if (!_data.TryRemove(key, out var _))
                {
                    return false;
                }

                return _data.TryAdd(key, entry);
            }

            return true;
        }
        else if (item.IsSection(out IConfigurationSection? section))
        {
            // Remove section and replace with value.
            if (path.Count == 1)
            {
                if (!_data.TryRemove(key, out var _))
                {
                    return false;
                }

                return _data.TryAdd(key, new ConfigurationValue(path, value, Name));
            }
            else
            {
                section[path] = value;
            }
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public IConfigurationEntry? GetEntry(Path path)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        IConfigurationEntry? item = default;
        Key key = path[0];

        if (_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            item = entry switch
            {
                IConfigurationSection section => section.GetEntry(path),
                IConfigurationValue value => value,
                _ => default
            };
        }

        return item;
    }

    /// <inheritdoc />
    public virtual IEnumerable<IConfigurationEntry> GetEntries()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return _data.Values;
    }

    protected abstract Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default);

    protected virtual ValueTask OnDisposeAsync(IEnumerable<IConfigurationEntry> entries)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public virtual void Load()
    {
        try
        {
            LoadAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            throw;
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationTokenSource.Token, 
            cancellationToken);

        Dictionary<Path, string?> entries = new Dictionary<Path, string?>(KeyComparer.FromComparison(_comparison));

        try
        {
            _isLoading = true;

            await OnLoadAsync(entries, cancellationTokenSource.Token).ConfigureAwait(false);

            _data.Clear();

            foreach ((Path path, string? value) in entries)
            {
                if (TrySet(path, value))
                {
                    // TODO: Notify of set issue
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("The operation timed out ", exception);
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            await OnDisposeAsync(_data.Values);

            _data.Clear();

            _isDisposed = true;
            _isLoading = false;

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null

            GC.SuppressFinalize(this);
        }
    }

    public void Dispose()
    {
        DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }


    partial class DebuggerView
    {
        private readonly ConfigurationProvider _provider;
        public DebuggerView(ConfigurationProvider provider)
        {
            _provider = provider;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public IConfigurationEntry[] Entries
        {
            get
            {
                IConfigurationEntry[] entries = new IConfigurationEntry[_provider._data.Count];

                int i = 0;
                foreach (var entry in _provider.GetEntries())
                {
                    entries[i] = entry;
                    i++;
                }

                return entries;
            }
        }
    }
}