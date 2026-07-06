using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Security.DataProtection;

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
/// Time is read through an injected <see cref="TimeProvider"/> so rotation and grace are
/// unit-testable without real delays.
/// </para>
/// </remarks>
internal sealed class KeyRing
{
    private readonly IKeyRepository _repository;
    private readonly TimeProvider _time;
    private readonly TimeSpan _keyLifetime;
    private readonly TimeSpan _gracePeriod;
    private readonly object _sync = new();

    private Dictionary<Guid, ManagedKey> _keys;

    public KeyRing(IKeyRepository repository, TimeProvider time, TimeSpan keyLifetime, TimeSpan gracePeriod)
    {
        _repository = repository;
        _time = time;
        _keyLifetime = keyLifetime;
        _gracePeriod = gracePeriod;
        _keys = LoadKeys();
    }

    /// <summary>Returns the key that should protect new payloads, creating one if necessary.</summary>
    public ManagedKey GetActiveKey()
    {
        DateTimeOffset now = _time.GetUtcNow();

        lock (_sync)
        {
            ManagedKey? active = FindActive(now);
            if (active is not null)
            {
                return active;
            }

            // Another node may have created an active key since we last loaded.
            _keys = LoadKeys();
            active = FindActive(now);
            if (active is not null)
            {
                return active;
            }

            ManagedKey created = CreateKey(now);
            _repository.StoreKey(KeySerializer.Serialize(created));
            _keys[created.KeyId] = created;
            return created;
        }
    }

    /// <summary>Resolves the key that produced a payload, enforcing revocation and the grace window.</summary>
    /// <exception cref="DataProtectionException">The key is unknown, revoked, or past its grace window.</exception>
    public ManagedKey ResolveForUnprotect(Guid keyId)
    {
        ManagedKey? key = Lookup(keyId);
        if (key is null)
        {
            // The payload may name a key another node created after our snapshot; reload once.
            lock (_sync)
            {
                if (!_keys.ContainsKey(keyId))
                {
                    _keys = LoadKeys();
                }
            }

            key = Lookup(keyId);
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

    private ManagedKey? Lookup(Guid keyId)
    {
        lock (_sync)
        {
            return _keys.TryGetValue(keyId, out ManagedKey? key) ? key : null;
        }
    }

    private ManagedKey? FindActive(DateTimeOffset now)
    {
        ManagedKey? best = null;
        foreach (ManagedKey key in _keys.Values)
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

    private Dictionary<Guid, ManagedKey> LoadKeys()
    {
        Dictionary<Guid, ManagedKey> map = new();
        foreach (KeyDocument document in _repository.GetAllKeys())
        {
            if (KeySerializer.TryDeserialize(document.Content.Span, out ManagedKey? key) && key is not null)
            {
                map[key.KeyId] = key;
            }
        }

        return map;
    }
}
