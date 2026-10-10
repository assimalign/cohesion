using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

/// <summary>
/// A thread-safe in-memory <see cref="IKeyRepository"/> that counts full reads, and can hold a
/// read open or make it fail, so tests can observe how often the key ring reloads and what
/// other callers do while a reload runs.
/// </summary>
internal sealed class CountingKeyRepository : IKeyRepository
{
    private readonly Lock _sync = new();
    private readonly InMemoryKeyRepository _inner = new();
    private readonly ManualResetEventSlim _loadStarted = new(false);
    private int _loadCount;
    private volatile ManualResetEventSlim? _gate;
    private volatile Exception? _failure;

    /// <summary>Gets the number of <see cref="GetAllKeys"/> calls so far.</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>
    /// Holds every later read until <paramref name="gate"/> is set. <see cref="WaitForLoad"/>
    /// returns once the next read has started.
    /// </summary>
    public void HoldLoadsUntil(ManualResetEventSlim gate)
    {
        _loadStarted.Reset();
        _gate = gate;
    }

    /// <summary>Makes every later read throw <paramref name="failure"/>.</summary>
    public void FailLoadsWith(Exception failure) => _failure = failure;

    /// <summary>Waits until a read has started since the last <see cref="HoldLoadsUntil"/>.</summary>
    public bool WaitForLoad(TimeSpan timeout) => _loadStarted.Wait(timeout);

    public IReadOnlyList<KeyDocument> GetAllKeys()
    {
        Interlocked.Increment(ref _loadCount);
        _loadStarted.Set();

        if (_gate is { } gate && !gate.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The test never released the held key-repository read.");
        }

        if (_failure is { } failure)
        {
            throw failure;
        }

        lock (_sync)
        {
            return _inner.GetAllKeys();
        }
    }

    public void StoreKey(KeyDocument key)
    {
        lock (_sync)
        {
            _inner.StoreKey(key);
        }
    }
}
