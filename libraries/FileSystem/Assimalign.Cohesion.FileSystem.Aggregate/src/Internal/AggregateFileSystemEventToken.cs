using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Fans an aggregate-level <see cref="IFileSystem.Watch"/> registration out across every
/// mounted provider's own watch token, remapping the event paths back into aggregate-space
/// before invoking subscriber callbacks.
/// </summary>
/// <remarks>
/// The aggregate-side <see cref="Glob"/> filter is applied AFTER path remapping so callers can
/// write patterns against the aggregate's virtual layout (e.g. <c>/data/**/*.log</c>) without
/// caring where each underlying provider is mounted.
/// </remarks>
internal sealed class AggregateFileSystemEventToken : IFileSystemEventToken, IDisposable
{
    private readonly Glob? _aggregateGlob;
    private readonly List<MountSubscription> _mountSubscriptions;
    private readonly object _gate = new();
    private bool _disposed;

    // A "match everything" glob handed to each underlying mount so it surfaces every change.
    // We filter against the (aggregate-side) glob at dispatch time after remapping the path.
    private static readonly Glob CatchAllGlob = Glob.Parse("/**");

    public AggregateFileSystemEventToken(IReadOnlyList<AggregateMount> mounts, Glob? aggregateGlob)
    {
        _aggregateGlob = aggregateGlob;
        _mountSubscriptions = new List<MountSubscription>(mounts.Count);

        foreach (var mount in mounts)
        {
            // The aggregate glob is matched at dispatch time (after path remapping). The mount
            // sees a permissive glob so it doesn't filter on its own — some providers (notably
            // InMemory) default a null glob to the directory's exact path, which would drop
            // every child event.
            var mountToken = mount.FileSystem.Watch(CatchAllGlob);
            _mountSubscriptions.Add(new MountSubscription(mount, mountToken));
        }
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Register<object>(callback: e => callback(e.State), state, RegisterFor.Change);
    }

    /// <inheritdoc />
    public IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state)
        => Register(callback, state, RegisterFor.Change);

    /// <inheritdoc />
    public IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state)
        => Register(callback, state, RegisterFor.Create);

    /// <inheritdoc />
    public IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state)
        => Register(callback, state, RegisterFor.Delete);

    /// <inheritdoc />
    public IDisposable OnRename<T>(Action<FileSystemRenameEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed)
        {
            return new NoopRegistration();
        }

        var registrations = new List<IDisposable>(_mountSubscriptions.Count);
        lock (_gate)
        {
            foreach (var sub in _mountSubscriptions)
            {
                var mount = sub.Mount;
                IDisposable disp = sub.Token.OnRename<T>(
                    e =>
                    {
                        // Remap both the old and new path into aggregate-space.
                        FileSystemPath oldAgg = mount.ToAggregatePath(e.OldPath);
                        FileSystemPath newAgg = mount.ToAggregatePath(e.Path);
                        if (_aggregateGlob is not null && !_aggregateGlob.IsMatch(newAgg) && !_aggregateGlob.IsMatch(oldAgg))
                        {
                            return;
                        }
                        callback(new FileSystemRenameEvent<T?>(oldAgg, newAgg, state, e.EventType));
                    },
                    state);
                registrations.Add(disp);
            }
        }

        return new CompositeRegistration(registrations);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (_gate)
        {
            foreach (var sub in _mountSubscriptions)
            {
                if (sub.Token is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch { /* best-effort */ }
                }
            }
            _mountSubscriptions.Clear();
        }
    }

    private IDisposable Register<T>(Action<FileSystemEvent<T?>> callback, T? state, RegisterFor kind)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed)
        {
            return new NoopRegistration();
        }

        var registrations = new List<IDisposable>(_mountSubscriptions.Count);
        lock (_gate)
        {
            foreach (var sub in _mountSubscriptions)
            {
                var mount = sub.Mount;
                Action<FileSystemEvent<T?>> wrappedCallback = e =>
                {
                    FileSystemPath aggregatePath = mount.ToAggregatePath(e.Path);
                    if (_aggregateGlob is not null && !_aggregateGlob.IsMatch(aggregatePath))
                    {
                        return;
                    }
                    callback(new FileSystemEvent<T?>(aggregatePath, state, e.EventType));
                };

                IDisposable disp = kind switch
                {
                    RegisterFor.Change => sub.Token.OnChange(wrappedCallback, state),
                    RegisterFor.Create => sub.Token.OnCreate(wrappedCallback, state),
                    RegisterFor.Delete => sub.Token.OnDelete(wrappedCallback, state),
                    _ => new NoopRegistration(),
                };
                registrations.Add(disp);
            }
        }

        return new CompositeRegistration(registrations);
    }

    private enum RegisterFor { Change, Create, Delete }

    private sealed class MountSubscription
    {
        public MountSubscription(AggregateMount mount, IFileSystemEventToken token)
        {
            Mount = mount;
            Token = token;
        }
        public AggregateMount Mount { get; }
        public IFileSystemEventToken Token { get; }
    }

    private sealed class CompositeRegistration : IDisposable
    {
        private readonly List<IDisposable> _inner;
        private bool _disposed;

        public CompositeRegistration(List<IDisposable> inner) { _inner = inner; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var d in _inner)
            {
                try { d.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    private sealed class NoopRegistration : IDisposable
    {
        public void Dispose() { }
    }
}
