using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// The <see cref="EventListener"/> behind <see cref="EventSourceLoggerFactoryExtensions"/>: enables every
/// matching event source at the level its logger accepts and writes each event as an
/// <see cref="ILoggerEntry"/>.
/// </summary>
/// <remarks>
/// Both callbacks run inside the instrumented code: <see cref="OnEventSourceCreated"/> during the event
/// source's construction (often a type initializer) and <see cref="OnEventWritten"/> on the thread that
/// raised the event. EventSource rethrows a listener's exception into that code, so neither callback lets
/// one escape.
/// </remarks>
internal sealed class EventSourceLogForwarder : EventListener
{
    // EventListener's constructor reports every existing event source through OnEventSourceCreated before
    // this type's constructor body runs. Field initializers run before the base constructor, so these are
    // ready for it: sources seen that early wait in _pendingSources until the factory and prefixes are set.
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<EventSource, ILogger> _loggers = new();
    private List<EventSource>? _pendingSources = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly string[] _sourcePrefixes;
    private int _disposed;

    [ThreadStatic]
    private static bool _isForwarding;

    public EventSourceLogForwarder(ILoggerFactory loggerFactory, string[] sourcePrefixes)
    {
        _loggerFactory = loggerFactory;
        _sourcePrefixes = sourcePrefixes;

        List<EventSource> pendingSources;

        lock (_gate)
        {
            pendingSources = _pendingSources!;

            // Release semantics: a thread that reads null takes the direct path and must see the fields above.
            Volatile.Write(ref _pendingSources, null);
        }

        foreach (EventSource eventSource in pendingSources)
        {
            Attach(eventSource);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose();
        _loggers.Clear();
    }

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (Volatile.Read(ref _pendingSources) is not null)
        {
            lock (_gate)
            {
                if (_pendingSources is not null)
                {
                    _pendingSources.Add(eventSource);
                    return;
                }
            }
        }

        Attach(eventSource);
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_isForwarding ||
            Volatile.Read(ref _disposed) != 0 ||
            !_loggers.TryGetValue(eventData.EventSource, out ILogger? logger) ||
            EventSourceLogMapper.IsCounterPayload(eventData))
        {
            return;
        }

        _isForwarding = true;

        try
        {
            LogLevel level = EventSourceLogMapper.GetLogLevel(eventData);

            if (logger.IsEnabled(level))
            {
                logger.Log(EventSourceLogMapper.CreateEntry(eventData, level));
            }
        }
        catch (Exception)
        {
            // Deliberately broad: EventSource rethrows a listener's exception as an EventSourceException into
            // the code that raised the event (a connection's receive loop, for example). A failing sink must
            // cost that event, never the instrumented operation.
        }
        finally
        {
            _isForwarding = false;
        }
    }

    private void Attach(EventSource eventSource)
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsForwarded(eventSource.Name))
        {
            return;
        }

        try
        {
            ILogger logger = _loggerFactory.Create(eventSource.Name);

            if (EventSourceLogMapper.GetEnabledLevel(logger) is not { } level)
            {
                // The factory accepts nothing for this category, so the source stays disabled and free.
                return;
            }

            _loggers[eventSource] = logger;
            EnableEvents(eventSource, level, EventKeywords.All);
        }
        catch (Exception)
        {
            // Deliberately broad: this runs while the event source is being constructed, usually inside a
            // type initializer. A disposed factory or a provider that throws from Create must leave the
            // source unforwarded, not make the instrumented library's type unusable.
            _loggers.TryRemove(eventSource, out _);
        }
    }

    private bool IsForwarded(string eventSourceName)
    {
        foreach (string prefix in _sourcePrefixes)
        {
            if (eventSourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
