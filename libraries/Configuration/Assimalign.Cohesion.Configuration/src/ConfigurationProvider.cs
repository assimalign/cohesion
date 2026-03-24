using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

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

        if (_isLoading || GetEntry(path) is not IConfigurationValue configurationValue)
        {
            return false;
        }

        string? current = configurationValue.Value;

        if (current is null)
        {
            return false;
        }

        value = current;

        return true;
    }

    /// <inheritdoc />
    public bool TrySet(Path path, string? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_isLoading || _isReadOnly)
        {
            return false;
        }

        return TrySetCore(path, value, ignoreReadOnly: false);
    }

    /// <inheritdoc />
    public IConfigurationEntry? GetEntry(Path path)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_isLoading || path.IsEmpty)
        {
            return null;
        }

        Key key = path[0];

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            return null;
        }

        return entry switch
        {
            ConfigurationSection section when path.IsComposite => GetVisibleEntry(section.GetEntry(path.Subpath(1))),
            ConfigurationSection section => section,
            IConfigurationValue value when !path.IsComposite && value.Value is not null => value,
            _ => null
        };
    }

    /// <inheritdoc />
    public virtual IEnumerable<IConfigurationEntry> GetEntries()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_isLoading)
        {
            return [];
        }

        return EnumerateEntries();
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
                TrySetCore(path, value, ignoreReadOnly: true);
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

            GC.SuppressFinalize(this);
        }
    }

    public void Dispose()
    {
        DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private IEnumerable<IConfigurationEntry> EnumerateEntries()
    {
        foreach (IConfigurationEntry entry in _data.Values)
        {
            if (entry is IConfigurationValue value && value.Value is null)
            {
                continue;
            }

            yield return entry;
        }
    }

    private bool TrySetCore(Path path, string? value, bool ignoreReadOnly)
    {
        if (path.IsEmpty)
        {
            return false;
        }

        return value is null
            ? TryRemoveCore(path)
            : TrySetValueCore(path, value, ignoreReadOnly);
    }

    private bool TrySetValueCore(Path path, string value, bool ignoreReadOnly)
    {
        Key key = path[0];

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            if (path.Count > 1)
            {
                Path entryPath = path.Subpath(0, 1);
                var section = new ConfigurationSection(entryPath, Name, _comparison, _isReadOnly);

                section.SetValue(path.Subpath(1), value, ignoreReadOnly: true);

                return _data.TryAdd(key, section);
            }

            return _data.TryAdd(key, new ConfigurationValue(path, value, Name, _isReadOnly));
        }

        if (entry is ConfigurationValue configurationValue)
        {
            if (path.Count == 1)
            {
                return configurationValue.SetValue(value, ignoreReadOnly);
            }

            Path entryPath = path.Subpath(0, 1);
            var section = new ConfigurationSection(entryPath, Name, _comparison, _isReadOnly);

            configurationValue.NotifyLocalChanged();
            section.SetValue(path.Subpath(1), value, ignoreReadOnly: true);
            _data[key] = section;

            return true;
        }

        if (entry is ConfigurationSection configurationSection)
        {
            if (path.Count == 1)
            {
                configurationSection.NotifyLocalChanged();
                _data[key] = new ConfigurationValue(path, value, Name, _isReadOnly);

                return true;
            }

            return configurationSection.SetValue(path.Subpath(1), value, ignoreReadOnly);
        }

        return false;
    }

    private bool TryRemoveCore(Path path)
    {
        Key key = path[0];

        if (!_lookup.TryGetValue(key, out IConfigurationEntry? entry))
        {
            return false;
        }

        if (path.Count == 1)
        {
            if (!_data.TryRemove(key, out IConfigurationEntry? removed))
            {
                return false;
            }

            if (removed is ConfigurationEntry configurationEntry)
            {
                configurationEntry.NotifyLocalChanged();
            }

            return true;
        }

        return entry is ConfigurationSection section && section.Remove(path.Subpath(1));
    }

    private static IConfigurationEntry? GetVisibleEntry(IConfigurationEntry? entry)
    {
        return entry switch
        {
            IConfigurationValue value when value.Value is not null => value,
            IConfigurationSection section => section,
            _ => null
        };
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
