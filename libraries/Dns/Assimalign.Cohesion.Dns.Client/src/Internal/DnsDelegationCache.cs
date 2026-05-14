using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Caches NS-record-driven delegations so an iterative resolver can skip the root&rarr;TLD
/// walk for questions whose closest enclosing delegation is already known. Indexed by zone
/// name; lookups return the most-specific enclosing entry for a given QNAME.
/// </summary>
/// <remarks>
/// <para>
/// Each entry holds the list of authoritative <see cref="IPEndPoint"/>s for the zone and an
/// absolute expiration computed from the minimum NS TTL the resolver observed. RFC 1035
/// &#167; 4.3.2 + RFC 7719 govern the TTL semantics: NS records are cached per their TTL,
/// and the resolver MAY rely on the cache as long as the TTL hasn't elapsed.
/// </para>
/// <para>
/// Approximate-LRU eviction at capacity (same shape as
/// <see cref="DnsAnswerCache"/>): when full, drop the oldest-inserted entry. Thread-safety is
/// guarded by a single lock.
/// </para>
/// </remarks>
internal sealed class DnsDelegationCache
{
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<DnsName, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _order = new();

    public DnsDelegationCache(int capacity, TimeProvider? timeProvider = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }
        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = new Dictionary<DnsName, LinkedListNode<Entry>>(capacity);
    }

    /// <summary>Current number of cached delegations.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Looks up the closest enclosing cached delegation for <paramref name="qname"/>. Walks
    /// from <paramref name="qname"/> upward through its ancestors and returns the
    /// most-specific live entry, or <see langword="false"/> if none applies.
    /// </summary>
    public bool TryGetClosest(
        DnsName qname,
        [NotNullWhen(true)] out DnsName? zone,
        [NotNullWhen(true)] out IReadOnlyList<IPEndPoint>? endpoints)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DnsName current = qname;
        lock (_gate)
        {
            while (true)
            {
                if (_entries.TryGetValue(current, out LinkedListNode<Entry>? node))
                {
                    if (now < node.Value.ExpiresAtUtc)
                    {
                        zone = node.Value.Zone;
                        endpoints = node.Value.Endpoints;
                        return true;
                    }
                    // Expired — evict and continue searching upward.
                    _entries.Remove(current);
                    _order.Remove(node);
                }
                if (current.IsRoot)
                {
                    break;
                }
                current = DnsBailiwick.Parent(current);
            }
        }
        zone = null;
        endpoints = null;
        return false;
    }

    /// <summary>
    /// Stores a delegation for <paramref name="zone"/>. The TTL is the minimum across the
    /// NS records that produced this delegation; a zero TTL skips the insert per RFC 1035
    /// &#167; 4.3.2.
    /// </summary>
    public void Put(DnsName zone, IReadOnlyList<IPEndPoint> endpoints, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0 || ttl <= TimeSpan.Zero)
        {
            return;
        }

        DateTimeOffset expiresAt = _timeProvider.GetUtcNow() + ttl;

        lock (_gate)
        {
            if (_entries.TryGetValue(zone, out LinkedListNode<Entry>? existing))
            {
                _order.Remove(existing);
                _entries.Remove(zone);
            }

            if (_entries.Count >= _capacity)
            {
                LinkedListNode<Entry>? oldest = _order.First;
                if (oldest is not null)
                {
                    _entries.Remove(oldest.Value.Zone);
                    _order.Remove(oldest);
                }
            }

            LinkedListNode<Entry> node = _order.AddLast(new Entry(zone, endpoints, expiresAt));
            _entries[zone] = node;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    private sealed record Entry(DnsName Zone, IReadOnlyList<IPEndPoint> Endpoints, DateTimeOffset ExpiresAtUtc);
}
