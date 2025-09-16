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
    private readonly KeyComparison _comparison;
    private readonly Dictionary<Key, Either<IConfigurationValue, IConfigurationSection>> _data;
    private readonly Dictionary<Key, Either<IConfigurationValue, IConfigurationSection>>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Lock _lock;
    private readonly bool _isReadOnly;

    private bool _isLoaded;
    private bool _isLoading;

    protected ConfigurationProvider() : this(KeyComparison.OrdinalIgnoreCase, Timeout.InfiniteTimeSpan)
    {
    }

    protected ConfigurationProvider(TimeSpan timeout) : this(KeyComparison.OrdinalIgnoreCase, timeout)
    {
    }

    protected ConfigurationProvider(KeyComparison comparison, TimeSpan timeout, bool isReadOnly = false)
    {
        _comparison = comparison;
        _data = new Dictionary<Key, Either<IConfigurationValue, IConfigurationSection>>(KeyComparer.FromComparison(comparison));
        _lookup = _data.GetAlternateLookup<ReadOnlySpan<char>>();
        _cancellationTokenSource = new CancellationTokenSource(timeout);
        _isReadOnly = isReadOnly;
        _lock = new Lock();
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public bool TryGet(Path path, out string value)
    {
        value = null!;

        Key key = path[0];

        if (!_lookup.TryGetValue(key, out var either))
        {
            return false;
        }

        if (either.If(out IConfigurationValue val, out IConfigurationSection section))
        {
            if (path.IsComposite)
            {
                return false;
            }

            value = val?.Value!;
        }
        else
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
        if (_isReadOnly && !_isLoading)
        {
            return false;
        }

        Key key = path[0];

        // If No Entry
        if (!_lookup.TryGetValue(key, out var either))
        {
            if (path.Count > 1)
            {
                var entryPath = path.Subpath(0, 1);
                var entry = new ConfigurationSection(entryPath, this, _comparison, _isReadOnly);

                entry[path] = value;

                _data.Add(key, entry);
            }
            else
            {
                _data.Add(key, new ConfigurationValue(path, value, this, _isReadOnly));
            }

            return true;
        }
        else if (either.If(out IConfigurationValue val))
        {
            if (path.Count == 1)
            {
                val.Value = value;
            }
            else
            {
                var entryPath = path.Subpath(0, 1);
                var entry = new ConfigurationSection(entryPath, this, _comparison, _isReadOnly);

                entry[path] = value;

                // Remove the value and replace with a section.
                _data.Remove(key, out var old);
                _data.Add(key, entry);
            }

            return true;
        }
        else if (either.If(out IConfigurationSection section))
        {
            // Remove section and replace with value.
            if (path.Count == 1)
            {
                _data.Remove(key);
                _data.Add(key, new ConfigurationValue(path, value, this));
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
        IConfigurationEntry? entry = default;

        for (int i = 0; i < path.Count; i++)
        {
            Key key = path[i];

            if (!_data.TryGetValue(key, out Either<IConfigurationValue, IConfigurationSection>? either))
            {
                return entry;
            }

            if (either.If(out IConfigurationSection section))
            {
                if (path.Count > section.Path.Count)
                {
                    return section.GetEntry(path);
                }

                return section;
            }

            if (either.If(out IConfigurationValue value))
            {
                return value;
            }
        }

        return entry;
    }

    /// <inheritdoc />
    public virtual IEnumerable<IConfigurationEntry> GetEntries()
    {
        return _data.Values.Select(either =>
        {
            return either.If(out IConfigurationSection section, out IConfigurationValue value) ?
                (IConfigurationEntry)section :
                value;
        });
    }

    public abstract Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default);

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
        using var cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);

        var entries = new Dictionary<Path, string?>(KeyComparer.FromComparison(_comparison));

        try
        {
            _isLoading = true;

            await OnLoadAsync(entries, cancellationTokenSource.Token).ConfigureAwait(false);

            foreach (var (path, value) in entries)
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

    /// <inheritdoc />
    public virtual void Dispose()
    {
        DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        _data.Clear();
        return ValueTask.CompletedTask;
    }

    public void CheckIfLoaded()
    {
        if (_isLoaded)
        {
            return;
        }
         
        lock (_lock)
        {
            Load();
        }
    }
}