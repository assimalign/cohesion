using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Translates between EventSource and Cohesion logging: levels in both directions, and one
/// <see cref="EventWrittenEventArgs"/> into one <see cref="LoggerEntry"/>.
/// </summary>
internal static class EventSourceLogMapper
{
    /// <summary>The attribute holding <see cref="EventWrittenEventArgs.EventId"/>.</summary>
    public const string EventIdAttribute = "EventId";

    /// <summary>The attribute holding <see cref="EventWrittenEventArgs.EventName"/>.</summary>
    public const string EventNameAttribute = "EventName";

    /// <summary>The attribute holding a non-empty <see cref="EventWrittenEventArgs.ActivityId"/>.</summary>
    public const string ActivityIdAttribute = "ActivityId";

    // EventSource reports its own instrumentation errors (a payload that does not match its event method,
    // a manifest problem) to listeners as event 0, named "EventSourceMessage", at LogAlways.
    private const int eventSourceMessageId = 0;

    // The periodic payload EventCounters write to every listener of a source whose counters a tool enabled.
    private const string counterPayloadEventName = "EventCounters";

    /// <summary>
    /// Returns the most verbose <see cref="EventLevel"/> whose events <paramref name="logger"/> would accept,
    /// or <see langword="null"/> when it accepts none.
    /// </summary>
    public static EventLevel? GetEnabledLevel(ILogger logger)
    {
        if (logger.IsEnabled(LogLevel.Trace) || logger.IsEnabled(LogLevel.Debug))
        {
            return EventLevel.Verbose;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            return EventLevel.Informational;
        }

        if (logger.IsEnabled(LogLevel.Warning))
        {
            return EventLevel.Warning;
        }

        if (logger.IsEnabled(LogLevel.Error))
        {
            return EventLevel.Error;
        }

        return logger.IsEnabled(LogLevel.Critical) ? EventLevel.Critical : null;
    }

    /// <summary>
    /// Returns the <see cref="LogLevel"/> an event is written at.
    /// </summary>
    public static LogLevel GetLogLevel(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId == eventSourceMessageId)
        {
            return LogLevel.Warning;
        }

        return eventData.Level switch
        {
            EventLevel.Critical => LogLevel.Critical,
            EventLevel.Error => LogLevel.Error,
            EventLevel.Warning => LogLevel.Warning,
            EventLevel.Verbose => LogLevel.Debug,
            _ => LogLevel.Information,
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> for a periodic EventCounters payload, which is not forwarded.
    /// </summary>
    public static bool IsCounterPayload(EventWrittenEventArgs eventData)
        => string.Equals(eventData.EventName, counterPayloadEventName, StringComparison.Ordinal);

    /// <summary>
    /// Creates the entry for <paramref name="eventData"/> at <paramref name="level"/>.
    /// </summary>
    public static LoggerEntry CreateEntry(EventWrittenEventArgs eventData, LogLevel level)
    {
        ReadOnlyCollection<object?>? payload = eventData.Payload;
        ReadOnlyCollection<string>? payloadNames = eventData.PayloadNames;
        int payloadCount = payload is null || payloadNames is null ? 0 : Math.Min(payload.Count, payloadNames.Count);

        var attributes = new Dictionary<string, object?>(payloadCount + 3, StringComparer.Ordinal);

        for (int index = 0; index < payloadCount; index++)
        {
            attributes[payloadNames![index]] = payload![index];
        }

        // The payload is the event author's data, so it wins a name collision with the metadata keys.
        attributes.TryAdd(EventIdAttribute, eventData.EventId);

        if (eventData.EventName is { Length: > 0 } eventName)
        {
            attributes.TryAdd(EventNameAttribute, eventName);
        }

        if (eventData.ActivityId != Guid.Empty)
        {
            attributes.TryAdd(ActivityIdAttribute, eventData.ActivityId);
        }

        return new LoggerEntry(
            level: level,
            category: eventData.EventSource.Name,
            message: FormatMessage(eventData, payload),
            attributes: attributes,
            timestamp: ToUtc(eventData.TimeStamp));
    }

    private static string FormatMessage(EventWrittenEventArgs eventData, ReadOnlyCollection<object?>? payload)
    {
        string? template = eventData.Message;

        // EventSource's own error report carries finished text, which may contain braces of its own.
        if (eventData.EventId == eventSourceMessageId)
        {
            return template ?? (payload is { Count: > 0 } && payload[0] is string text ? text : eventData.EventName ?? string.Empty);
        }

        if (!string.IsNullOrEmpty(template))
        {
            if (payload is null || payload.Count == 0)
            {
                return template;
            }

            object?[] arguments = new object?[payload.Count];
            payload.CopyTo(arguments, 0);

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, arguments);
            }
            catch (FormatException)
            {
                // A template whose placeholders do not match its payload falls back to the event name; the
                // payload itself is still in the attributes.
            }
        }

        return eventData.EventName ?? string.Empty;
    }

    private static DateTimeOffset ToUtc(DateTime timestamp) => timestamp.Kind switch
    {
        DateTimeKind.Unspecified => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
        _ => new DateTimeOffset(timestamp.ToUniversalTime()),
    };
}
