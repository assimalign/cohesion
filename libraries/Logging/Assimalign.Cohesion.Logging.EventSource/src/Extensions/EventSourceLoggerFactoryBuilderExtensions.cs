using System;

using Assimalign.Cohesion.Logging.Internal;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Registers <see cref="System.Diagnostics.Tracing.EventSource"/> forwarding on an
/// <see cref="ILoggerFactoryBuilder"/>, so the built factory owns it.
/// </summary>
/// <remarks>
/// This is the composition-time form of
/// <see cref="EventSourceLoggerFactoryExtensions.ForwardEventSources"/>: the same forwarder, created by
/// the factory as the last step of its construction and disposed by the factory before its providers.
/// Nothing is left for the application to hold, and events raised while the rest of the application
/// starts (listeners binding, connections opening) are forwarded because forwarding begins before any
/// of that code runs.
/// </remarks>
public static class EventSourceLoggerFactoryBuilderExtensions
{
    /// <param name="builder">The builder whose factory forwards the events.</param>
    extension(ILoggerFactoryBuilder builder)
    {
        /// <summary>
        /// Registers a forwarder that writes the events of every matching event source, including sources
        /// created later, to loggers created from the built factory.
        /// </summary>
        /// <param name="options">
        /// The sources to forward, or <see langword="null"/> to forward every Cohesion event source. The
        /// prefixes are copied now; changing the options afterwards has no effect.
        /// </param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <see cref="EventSourceForwardingOptions.Sources"/> is empty or contains a null, empty, or
        /// whitespace prefix.
        /// </exception>
        /// <exception cref="InvalidOperationException">The builder has already been used to build a factory.</exception>
        /// <remarks>
        /// Level mapping, entry shape, and failure behavior are those of
        /// <see cref="EventSourceLoggerFactoryExtensions.ForwardEventSources"/>. The forwarder is named
        /// <c>EventSource</c> and forwarder names are unique within a factory, so registering this twice makes
        /// <see cref="ILoggerFactoryBuilder.Build"/> throw <see cref="InvalidOperationException"/>; put every
        /// prefix in one registration.
        /// </remarks>
        public ILoggerFactoryBuilder AddEventSourceForwarding(EventSourceForwardingOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            string[] sourcePrefixes = EventSourceLogForwarder.GetSourcePrefixes(options);

            return builder.AddForwarder(loggerFactory => new EventSourceLogForwarder(loggerFactory, sourcePrefixes));
        }
    }
}
