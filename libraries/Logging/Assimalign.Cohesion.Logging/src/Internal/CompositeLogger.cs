using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Fans out a single <see cref="ILoggerEntry"/> to every registered provider logger.
/// </summary>
/// <remarks>
/// The composite owns the per-provider rule resolution (one resolved
/// <see cref="LoggerFilterRule"/> per registered provider for this category) and the
/// enrichment pipeline. Each provider is gated independently: an entry can be accepted by
/// provider A and rejected by provider B based on each provider's resolved rule. Failures from
/// a single underlying logger are isolated: an exception from one sink never aborts fan-out to
/// the others.
/// </remarks>
internal sealed class CompositeLogger : Logger
{
    private readonly ILogger[] _underlying;
    private readonly IReadOnlyList<ILoggerEnricher> _enrichers;
    private readonly LogLevel[] _perProviderLevel;
    private readonly ILoggerFilter?[] _perProviderFilter;

    public CompositeLogger(
        string category,
        ILogger[] underlying,
        IReadOnlyList<ILoggerEnricher> enrichers,
        LogLevel[] perProviderLevel,
        ILoggerFilter?[] perProviderFilter)
        : base(category)
    {
        _underlying = underlying;
        _enrichers = enrichers;
        _perProviderLevel = perProviderLevel;
        _perProviderFilter = perProviderFilter;
    }

    public override bool IsEnabled(LogLevel level)
    {
        if (level == LogLevel.None)
        {
            return false;
        }

        for (int i = 0; i < _underlying.Length; i++)
        {
            if (level >= _perProviderLevel[i] && _underlying[i].IsEnabled(level))
            {
                return true;
            }
        }

        return false;
    }

    protected override void WriteCore(ILoggerEntry entry)
    {
        if (entry.Level == LogLevel.None)
        {
            return;
        }

        // Enrich once for the whole fan-out so providers see identical attribute snapshots.
        var enriched = ApplyEnrichment(entry);

        for (int i = 0; i < _underlying.Length; i++)
        {
            if (entry.Level < _perProviderLevel[i])
            {
                continue;
            }

            if (_perProviderFilter[i] is { } filter)
            {
                try
                {
                    if (!filter.ShouldLog(enriched))
                    {
                        continue;
                    }
                }
                catch
                {
                    // Bad filter cannot abort the pipeline; treat the throw as admit.
                }
            }

            try
            {
                _underlying[i].Log(enriched);
            }
            catch
            {
                // Provider failures are isolated; healthy sinks must still get the entry.
            }
        }
    }

    protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
    {
        var enriched = ApplyEnrichment(entry);

        // The composite does NOT emit the seed itself; each underlying provider's BeginScope
        // handles seed emission for its own sink, so the seed appears exactly once per sink.
        var underlyingScopes = new IScopedLogger[_underlying.Length];
        for (int i = 0; i < _underlying.Length; i++)
        {
            try
            {
                underlyingScopes[i] = _underlying[i].BeginScope(enriched);
            }
            catch
            {
                underlyingScopes[i] = NoopScopedLogger.Instance;
            }
        }

        return new ScopedCompositeLogger(this, Category, enriched.Id, underlyingScopes);
    }

    private ILoggerEntry ApplyEnrichment(ILoggerEntry entry)
    {
        if (_enrichers.Count == 0)
        {
            return entry;
        }

        var attributes = new Dictionary<string, object?>(entry.Attributes, StringComparer.Ordinal);

        for (int i = 0; i < _enrichers.Count; i++)
        {
            try
            {
                _enrichers[i].Enrich(entry, new EnricherAttributeView(attributes));
            }
            catch
            {
                // Enricher failures are non-fatal; the entry still ships.
            }
        }

        if (attributes.Count == entry.Attributes.Count)
        {
            return entry;
        }

        return new LoggerEntry(
            level: entry.Level,
            category: entry.Category,
            message: entry.Message,
            exception: entry.Exception,
            attributes: attributes,
            parentId: entry.ParentId,
            id: entry.Id,
            timestamp: entry.Timestamp);
    }

    /// <summary>
    /// Restricted view on the enrichment attribute bag: enrichers may add new keys but cannot
    /// overwrite values supplied by the entry author.
    /// </summary>
    private sealed class EnricherAttributeView : IDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> _inner;

        public EnricherAttributeView(Dictionary<string, object?> inner)
        {
            _inner = inner;
        }

        public object? this[string key]
        {
            get => _inner[key];
            set
            {
                if (_inner.ContainsKey(key))
                {
                    return;
                }
                _inner[key] = value;
            }
        }

        public ICollection<string> Keys => _inner.Keys;
        public ICollection<object?> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool IsReadOnly => false;

        public void Add(string key, object? value)
        {
            if (_inner.ContainsKey(key))
            {
                return;
            }
            _inner.Add(key, value);
        }

        public void Add(KeyValuePair<string, object?> item) => Add(item.Key, item.Value);
        public void Clear() => _inner.Clear();
        public bool Contains(KeyValuePair<string, object?> item) => ((ICollection<KeyValuePair<string, object?>>)_inner).Contains(item);
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, object?>>)_inner).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _inner.GetEnumerator();
        public bool Remove(string key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<string, object?> item) => ((ICollection<KeyValuePair<string, object?>>)_inner).Remove(item);
        public bool TryGetValue(string key, out object? value) => _inner.TryGetValue(key, out value);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }
}
