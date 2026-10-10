using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace Assimalign.Cohesion.Security.DataProtection.Internal;

/// <summary>
/// The in-memory view of the persisted keys, plus the rotation and grace-period rules that
/// decide which key signs new payloads and which retired keys may still unprotect. Backed by
/// an <see cref="IKeyRepository"/> for durability and cross-node sharing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rotation.</b> <see cref="GetActiveKey"/> returns the newest non-revoked key whose
/// activation/expiration window contains "now". When none qualifies (first run, or the active
/// key just expired), it creates a fresh key, persists it, and returns it. Nodes therefore
/// rotate lazily on first protect after expiry, with no scheduler.
/// </para>
/// <para>
/// <b>Grace.</b> <see cref="ResolveForUnprotect(Guid)"/> accepts a key until its expiration
/// plus the configured grace period, so payloads minted just before a rotation keep validating
/// across the fleet. Revoked keys are rejected immediately regardless of the window.
/// </para>
/// <para>
/// <b>Snapshot and reloads.</b> The keys live in an immutable snapshot that readers take
/// without a lock. Only a reload or a key creation replaces it, one at a time under the
/// reload lock, so protecting or unprotecting with a key the ring holds never waits on a
/// repository read. A payload whose key id is not in the snapshot reloads the repository at
/// most once per unknown-key reload interval, because the payload's sender chooses that id:
/// inside the interval an unknown id is reported unknown after one timestamp read, without
/// the lock.
/// </para>
/// <para>
/// Time is read through an injected <see cref="TimeProvider"/> so rotation, grace, and the
/// reload throttle are unit-testable without real delays.
/// </para>
/// </remarks>
internal sealed class KeyRing
{
    private readonly IKeyRepository _repository;
    private readonly TimeProvider _time;
    private readonly TimeSpan _keyLifetime;
    private readonly TimeSpan _gracePeriod;
    private readonly long _unknownKeyReloadInterval;
    private readonly Lock _reloadSync = new();

    // Replaced whole under _reloadSync, never mutated after publication, read without a lock.
    private volatile FrozenDictionary<Guid, ManagedKey> _keys;

    // The TimeProvider timestamp before which an unknown key id may not reload the repository.
    // Written under _reloadSync and read without it. A stale read only sends a caller to the
    // lock, where the value is read again.
    private long _unknownKeyReloadNotBefore = long.MinValue;

    public KeyRing(
        IKeyRepository repository,
        TimeProvider time,
        TimeSpan keyLifetime,
        TimeSpan gracePeriod,
        TimeSpan unknownKeyReloadInterval)
    {
        _repository = repository;
        _time = time;
        _keyLifetime = keyLifetime;
        _gracePeriod = gracePeriod;
        _unknownKeyReloadInterval = ToTimestampUnits(unknownKeyReloadInterval, time.TimestampFrequency);

        // The initial load does not start the throttle window: the first unknown id after
        // startup still reloads at once, so a node that starts just before another node
        // rotates picks the new key up on first sight.
        _keys = LoadKeys();
    }

    /// <summary>Returns the key that should protect new payloads, creating one if necessary.</summary>
    public ManagedKey GetActiveKey()
    {
        DateTimeOffset now = _time.GetUtcNow();

        ManagedKey? active = FindActive(_keys, now);
        if (active is not null)
        {
            return active;
        }

        lock (_reloadSync)
        {
            // Another caller may have reloaded or created a key while this one waited.
            active = FindActive(_keys, now);
            if (active is not null)
            {
                return active;
            }

            // Another node may have created an active key since we last loaded. This reload is
            // not throttled: it runs only while the snapshot holds no active key, and the key
            // it finds or creates ends that.
            FrozenDictionary<Guid, ManagedKey> loaded = LoadKeys();
            _keys = loaded;

            active = FindActive(loaded, now);
            if (active is not null)
            {
                return active;
            }

            ManagedKey created = CreateKey(now);
            _repository.StoreKey(KeySerializer.Serialize(created));
            _keys = WithKey(loaded, created);
            return created;
        }
    }

    /// <summary>Resolves the key that produced a payload, enforcing revocation and the grace window.</summary>
    /// <exception cref="DataProtectionException">The key is unknown, revoked, or past its grace window.</exception>
    public ManagedKey ResolveForUnprotect(Guid keyId)
    {
        if (!_keys.TryGetValue(keyId, out ManagedKey? key))
        {
            key = ReloadForUnknownKey(keyId);
        }

        if (key is null)
        {
            throw new DataProtectionException("The key that produced this payload is unknown to the ring.");
        }

        if (key.IsRevoked)
        {
            throw new DataProtectionException("The key that produced this payload has been revoked.");
        }

        if (_time.GetUtcNow() >= key.ExpiresAt + _gracePeriod)
        {
            throw new DataProtectionException("The key that produced this payload has aged out of the unprotect grace window.");
        }

        return key;
    }

    // The payload may name a key another node created after our snapshot, so an unknown id
    // reloads the repository. The id comes from the payload's sender, so the reload is
    // throttled to one per interval, and callers that miss while it runs share its result.
    private ManagedKey? ReloadForUnknownKey(Guid keyId)
    {
        // Inside the window a miss costs one timestamp read: no lock, no repository read.
        if (_time.GetTimestamp() < Volatile.Read(ref _unknownKeyReloadNotBefore))
        {
            return null;
        }

        lock (_reloadSync)
        {
            // A caller that waited here shares the reload that ran while it waited.
            if (_keys.TryGetValue(keyId, out ManagedKey? key))
            {
                return key;
            }

            long started = _time.GetTimestamp();
            if (started < _unknownKeyReloadNotBefore)
            {
                return null;
            }

            FrozenDictionary<Guid, ManagedKey> loaded;
            try
            {
                loaded = LoadKeys();
            }
            finally
            {
                // The window is measured from the read's start, so a key written while the read
                // ran still resolves within one interval of its write. A failed read closes the
                // window too, so a repository that keeps failing is not read on every miss.
                Volatile.Write(ref _unknownKeyReloadNotBefore, AddSaturating(started, _unknownKeyReloadInterval));
            }

            _keys = loaded;
            return loaded.TryGetValue(keyId, out key) ? key : null;
        }
    }

    private static ManagedKey? FindActive(FrozenDictionary<Guid, ManagedKey> keys, DateTimeOffset now)
    {
        ManagedKey? best = null;
        foreach (ManagedKey key in keys.Values)
        {
            if (key.IsRevoked || now < key.ActivatedAt || now >= key.ExpiresAt)
            {
                continue;
            }

            if (best is null || key.ActivatedAt > best.ActivatedAt)
            {
                best = key;
            }
        }

        return best;
    }

    private ManagedKey CreateKey(DateTimeOffset now)
    {
        byte[] master = RandomNumberGenerator.GetBytes(ManagedKey.MasterLength);
        return new ManagedKey(Guid.NewGuid(), now, now, now + _keyLifetime, isRevoked: false, master);
    }

    private FrozenDictionary<Guid, ManagedKey> LoadKeys()
    {
        Dictionary<Guid, ManagedKey> map = new();
        foreach (KeyDocument document in _repository.GetAllKeys())
        {
            if (KeySerializer.TryDeserialize(document.Content.Span, out ManagedKey? key) && key is not null)
            {
                map[key.KeyId] = key;
            }
        }

        return map.ToFrozenDictionary();
    }

    private static FrozenDictionary<Guid, ManagedKey> WithKey(FrozenDictionary<Guid, ManagedKey> keys, ManagedKey key)
    {
        Dictionary<Guid, ManagedKey> map = new(keys);
        map[key.KeyId] = key;
        return map.ToFrozenDictionary();
    }

    private static long ToTimestampUnits(TimeSpan interval, long frequency)
    {
        double units = Math.Ceiling(interval.Ticks * ((double)frequency / TimeSpan.TicksPerSecond));
        return units >= long.MaxValue ? long.MaxValue : Math.Max(1L, (long)units);
    }

    private static long AddSaturating(long timestamp, long units)
    {
        return timestamp > long.MaxValue - units ? long.MaxValue : timestamp + units;
    }
}
