using System;

using Assimalign.Cohesion.Logging.Internal;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Forwards <see cref="System.Diagnostics.Tracing.EventSource"/> events into an <see cref="ILoggerFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cohesion libraries report their runtime diagnostics through internal event sources, never through
/// public logging types. Out-of-process tools (<c>dotnet-trace</c>, <c>dotnet-counters</c>, PerfView)
/// already see those sources by name. This is the in-process tap: an
/// <see cref="System.Diagnostics.Tracing.EventListener"/> that turns each event into an
/// <see cref="ILoggerEntry"/> on the factory's loggers.
/// </para>
/// <para>
/// Each forwarded event becomes one entry whose <see cref="ILoggerEntry.Category"/> is the event source
/// name, whose <see cref="ILoggerEntry.Message"/> is the event's formatted message template (or its name
/// when it has none), and whose <see cref="ILoggerEntry.Attributes"/> hold the event payload plus
/// <c>EventId</c>, <c>EventName</c>, and <c>ActivityId</c> when non-empty. A payload field keeps its
/// value when its name collides with one of those keys.
/// </para>
/// </remarks>
public static class EventSourceLoggerFactoryExtensions
{
    /// <param name="loggerFactory">The factory whose loggers receive the forwarded events.</param>
    extension(ILoggerFactory loggerFactory)
    {
        /// <summary>
        /// Starts forwarding the events of every matching event source, including sources created later,
        /// to loggers created from this factory.
        /// </summary>
        /// <param name="options">
        /// The sources to forward, or <see langword="null"/> to forward every Cohesion event source.
        /// </param>
        /// <returns>A handle that stops forwarding when disposed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <see cref="EventSourceForwardingOptions.Sources"/> is empty or contains a null, empty, or
        /// whitespace prefix.
        /// </exception>
        /// <remarks>
        /// <para>
        /// A source is enabled at <see cref="System.Diagnostics.Tracing.EventLevel.Verbose"/> when its logger
        /// accepts <see cref="LogLevel.Debug"/>, at <c>Informational</c> when it accepts
        /// <see cref="LogLevel.Information"/>, and so on; a source whose logger accepts nothing is not
        /// enabled, so its events cost nothing. Events map to levels as Critical, Error, and Warning to
        /// their namesakes, Informational and LogAlways to <see cref="LogLevel.Information"/>, and Verbose to
        /// <see cref="LogLevel.Debug"/>. EventSource's own error report (event 0,
        /// <c>EventSourceMessage</c>) maps to <see cref="LogLevel.Warning"/>.
        /// </para>
        /// <para>
        /// Events are written synchronously on the thread that raised them. An event raised while that
        /// thread is already forwarding (a sink that itself uses an instrumented library) is dropped rather
        /// than recursed into, and nothing a sink throws reaches the code that raised the event. Periodic
        /// <c>EventCounters</c> payloads are not forwarded. Call this once per factory; two handles
        /// forward every event twice.
        /// </para>
        /// <para>
        /// NativeAOT applications receive events only when published with
        /// <c>&lt;EventSourceSupport&gt;true&lt;/EventSourceSupport&gt;</c>.
        /// </para>
        /// </remarks>
        public IDisposable ForwardEventSources(EventSourceForwardingOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);

            string[] sourcePrefixes = [.. (options ?? new EventSourceForwardingOptions()).Sources];

            if (sourcePrefixes.Length == 0)
            {
                throw new ArgumentException("At least one event source name prefix is required.", nameof(options));
            }

            foreach (string prefix in sourcePrefixes)
            {
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    throw new ArgumentException("Event source name prefixes cannot be null, empty, or whitespace.", nameof(options));
                }
            }

            return new EventSourceLogForwarder(loggerFactory, sourcePrefixes);
        }
    }
}
