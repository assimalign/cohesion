using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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
    private readonly HashSet<CompositeRegistration> _registrations = new();
    private readonly Action<AggregateFileSystemEventToken> _onDispose;
    private int _disposed;

    // A "match everything" glob handed to each underlying mount so it surfaces every change.
    // We filter against the (aggregate-side) glob at dispatch time after remapping the path.
    private static readonly Glob CatchAllGlob = Glob.Parse("/**");

    public AggregateFileSystemEventToken(IReadOnlyList<AggregateMount> mounts, Glob? aggregateGlob, Action<AggregateFileSystemEventToken> onDispose)
    {
        _aggregateGlob = aggregateGlob;
        _onDispose = onDispose;
        _mountSubscriptions = new List<MountSubscription>(mounts.Count);

        try
        {
            foreach (var mount in mounts)
            {
                // Every token returned by these Watch calls is owned by this aggregate token.
                // Provider ownership is independent: borrowed mounts still create owned tokens.
                var mountToken = mount.FileSystem.Watch(CatchAllGlob);
                _mountSubscriptions.Add(new MountSubscription(mount, mountToken));
            }
        }
        catch
        {
            Dispose();
            throw;
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
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new NoopRegistration();
            }
            var registrations = new List<IDisposable>(_mountSubscriptions.Count);
            foreach (var sub in _mountSubscriptions)
            {
                var mount = sub.Mount;
                IDisposable disp = sub.Token.OnRename<T>(
                    e =>
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            return;
                        }
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
            var registration = new CompositeRegistration(registrations, RemoveRegistration);
            _registrations.Add(registration);
            return registration;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        MountSubscription[] subscriptions;
        CompositeRegistration[] registrations;
        lock (_gate)
        {
            subscriptions = _mountSubscriptions.ToArray();
            registrations = new CompositeRegistration[_registrations.Count];
            _registrations.CopyTo(registrations);
            _mountSubscriptions.Clear();
            _registrations.Clear();
        }

        // Child callbacks can be in flight. Dispose outside our gate so a child that waits
        // for its callbacks cannot deadlock with registration cleanup or user callbacks.
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
        foreach (var sub in subscriptions)
        {
            if (sub.Token is IDisposable disposable)
            {
                // Preserve the aggregate's best-effort cleanup policy for third-party
                // tokens: one broken child must not keep later children alive.
                try { disposable.Dispose(); } catch { /* best-effort */ }
            }
        }
        _onDispose(this);
    }

    private void RemoveRegistration(CompositeRegistration registration)
    {
        lock (_gate)
        {
            _registrations.Remove(registration);
        }
    }

    private IDisposable Register<T>(Action<FileSystemEvent<T?>> callback, T? state, RegisterFor kind)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new NoopRegistration();
            }
            var registrations = new List<IDisposable>(_mountSubscriptions.Count);
            foreach (var sub in _mountSubscriptions)
            {
                var mount = sub.Mount;
                Action<FileSystemEvent<T?>> wrappedCallback = e =>
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }
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
            var registration = new CompositeRegistration(registrations, RemoveRegistration);
            _registrations.Add(registration);
            return registration;
        }
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
        private readonly Action<CompositeRegistration> _onDispose;
        private int _disposed;

        public CompositeRegistration(List<IDisposable> inner, Action<CompositeRegistration> onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            foreach (var d in _inner)
            {
                try { d.Dispose(); } catch { /* best-effort */ }
            }
            _onDispose(this);
        }
    }

    private sealed class NoopRegistration : IDisposable
    {
        public void Dispose() { }
    }
}
