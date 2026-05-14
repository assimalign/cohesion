using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// In-memory TTL-aware DNS answer cache. Keyed by <see cref="DnsQuestion"/>; stores the
/// full <see cref="DnsMessage"/> so callers see the same answer/authority/additional
/// sections the upstream produced. Eviction is governed by RFC 1035 &#167; 4.3.2 (positive)
/// and RFC 2308 &#167; 5 (negative).
/// </summary>
/// <remarks>
/// <para>
/// Capacity is enforced by approximate-LRU eviction: when the cache is full, a new
/// <see cref="Put"/> drops the oldest-inserted entry. This is intentionally simple &#8211;
/// precise LRU buys little against a workload dominated by repeated lookups of a small
/// working set, and the implementation cost shows up everywhere.
/// </para>
/// <para>
/// Thread-safety: all operations are guarded by a single lock. Cache lookups on the hit
/// path do not allocate.
/// </para>
/// </remarks>
internal sealed class DnsAnswerCache
{
    private readonly int _capacity;
    private readonly TimeSpan? _minTtl;
    private readonly TimeSpan? _maxTtl;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<DnsQuestion, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _order = new();

    public DnsAnswerCache(int capacity, TimeSpan? minTtl, TimeSpan? maxTtl, TimeProvider? timeProvider = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Cache capacity must be positive.");
        }
        if (minTtl is { } min && min < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minTtl), minTtl, "MinTtl must be non-negative.");
        }
        if (maxTtl is { } max && max < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTtl), maxTtl, "MaxTtl must be non-negative.");
        }
        if (minTtl is not null && maxTtl is not null && minTtl > maxTtl)
        {
            throw new ArgumentException("MinTtl must not exceed MaxTtl.", nameof(minTtl));
        }

        _capacity = capacity;
        _minTtl = minTtl;
        _maxTtl = maxTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = new Dictionary<DnsQuestion, LinkedListNode<Entry>>(capacity);
    }

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
    /// Looks up an entry by question. Returns <see langword="false"/> on miss or expired entry
    /// (expired entries are evicted on hit).
    /// </summary>
    public bool TryGet(DnsQuestion question, [NotNullWhen(true)] out DnsMessage? message)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(question, out LinkedListNode<Entry>? node))
            {
                if (_timeProvider.GetUtcNow() < node.Value.ExpiresAtUtc)
                {
                    message = node.Value.Message;
                    return true;
                }
                // Expired — evict.
                _entries.Remove(question);
                _order.Remove(node);
            }
        }
        message = null;
        return false;
    }

    /// <summary>
    /// Stores <paramref name="message"/> against <paramref name="question"/>. Computes the
    /// effective TTL per RFC 1035 §4.3.2 / RFC 2308 §5, clamps it to the configured floor /
    /// ceiling, and skips the insert when the effective TTL is zero.
    /// </summary>
    public void Put(DnsQuestion question, DnsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        TimeSpan ttl = ComputeCacheTtl(message);
        if (_minTtl is { } min && ttl < min)
        {
            ttl = min;
        }
        if (_maxTtl is { } max && ttl > max)
        {
            ttl = max;
        }
        if (ttl <= TimeSpan.Zero)
        {
            return; // RFC 1035 §4.3.2 — TTL=0 forbids caching.
        }

        DateTimeOffset expiresAt = _timeProvider.GetUtcNow() + ttl;

        lock (_gate)
        {
            if (_entries.TryGetValue(question, out LinkedListNode<Entry>? existing))
            {
                _order.Remove(existing);
                _entries.Remove(question);
            }

            if (_entries.Count >= _capacity)
            {
                // Approximate-LRU eviction: drop the head (oldest-inserted) entry.
                LinkedListNode<Entry>? oldest = _order.First;
                if (oldest is not null)
                {
                    _entries.Remove(oldest.Value.Question);
                    _order.Remove(oldest);
                }
            }

            LinkedListNode<Entry> node = _order.AddLast(new Entry(question, message, expiresAt));
            _entries[question] = node;
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

    /// <summary>
    /// Computes the effective cache TTL for a response message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Positive answers (NoError + at least one answer): the minimum TTL across the answer
    /// section, bounded by the minimum TTL across the authority-section NS records. RFC 1035
    /// §4.3.2 says "the value in the TTL field is interpreted as the maximum number of seconds
    /// the record can be cached."
    /// </para>
    /// <para>
    /// Negative answers (NXDOMAIN or NoError + no answer): per RFC 2308 §5 the negative-caching
    /// TTL is <c>min(SOA.TTL, SOA.MINIMUM)</c> drawn from the SOA record in the authority
    /// section. When there's no SOA (deliberately ANTI-RFC-2308 servers exist), the negative
    /// is treated as uncacheable (TTL = 0).
    /// </para>
    /// </remarks>
    internal static TimeSpan ComputeCacheTtl(DnsMessage message)
    {
        bool isNegative = message.Header.ResponseCode == DnsResponseCode.NXDomain || message.Answers.Count == 0;

        if (isNegative)
        {
            foreach (DnsRecord rr in message.Authorities)
            {
                if (rr is DnsSoaRecord soa)
                {
                    uint negTtl = Math.Min(rr.TimeToLive, soa.MinimumTtl);
                    return TimeSpan.FromSeconds(negTtl);
                }
            }
            return TimeSpan.Zero;
        }

        uint minTtl = uint.MaxValue;
        foreach (DnsRecord rr in message.Answers)
        {
            if (rr.TimeToLive < minTtl)
            {
                minTtl = rr.TimeToLive;
            }
        }
        foreach (DnsRecord rr in message.Authorities)
        {
            if (rr.Type == DnsRecordType.NS && rr.TimeToLive < minTtl)
            {
                minTtl = rr.TimeToLive;
            }
        }
        return minTtl == uint.MaxValue ? TimeSpan.Zero : TimeSpan.FromSeconds(minTtl);
    }

    private sealed record Entry(DnsQuestion Question, DnsMessage Message, DateTimeOffset ExpiresAtUtc);
}
