using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Fans out a single <see cref="ILogEntry"/> to every registered provider logger.
/// </summary>
/// <remarks>
/// The composite owns the per-category minimum-level decision (which respects category-specific
/// filters) and runs enrichers in registration order before fan-out. Failures from a single
/// underlying logger are isolated: the composite swallows them so a misbehaving sink cannot abort
/// fan-out to healthy sinks.
/// </remarks>
internal sealed class CompositeLogger : ILogger
{
    private readonly string _category;
    private readonly ILogger[] _underlying;
    private readonly IReadOnlyList<ILogEnricher> _enrichers;
    private readonly LogLevel _minimumLevel;

    public CompositeLogger(
        string category,
        ILogger[] underlying,
        IReadOnlyList<ILogEnricher> enrichers,
        LogLevel minimumLevel)
    {
        _category = category;
        _underlying = underlying;
        _enrichers = enrichers;
        _minimumLevel = minimumLevel;
    }

    public bool IsEnabled(LogLevel level)
    {
        if (level < _minimumLevel || level == LogLevel.None)
        {
            return false;
        }

        for (int i = 0; i < _underlying.Length; i++)
        {
            if (_underlying[i].IsEnabled(level))
            {
                return true;
            }
        }

        return false;
    }

    public void Log(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!IsEnabled(entry.Level))
        {
            return;
        }

        var enriched = ApplyEnrichment(entry);

        for (int i = 0; i < _underlying.Length; i++)
        {
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

    public IScopedLogger BeginScope(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var enriched = ApplyEnrichment(entry);

        // Open the seed entry's scope on the underlying loggers so providers that maintain scope
        // state (e.g. a JSON collector building a nested object) see the same parent.
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

        return new ScopedCompositeLogger(this, enriched.Id, underlyingScopes);
    }

    private ILogEntry ApplyEnrichment(ILogEntry entry)
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

        return new LogEntry(
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
