using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The values a session reads from <see cref="AmqpSessionOptions"/> once, when it begins, so later changes
/// to the options object cannot reach a session in flight.
/// </summary>
/// <param name="IncomingWindow">The number of transfer frames the session accepts before it widens its window again.</param>
/// <param name="HandleMax">The highest link handle the peer may use on the session.</param>
/// <param name="OfferedCapabilities">The capabilities the session's <c>begin</c> offers.</param>
/// <param name="DesiredCapabilities">The capabilities the session's <c>begin</c> desires.</param>
/// <param name="Properties">The properties the session's <c>begin</c> carries.</param>
internal sealed record AmqpSessionSettings(
    uint IncomingWindow,
    uint HandleMax,
    IReadOnlyList<AmqpSymbol>? OfferedCapabilities,
    IReadOnlyList<AmqpSymbol>? DesiredCapabilities,
    IReadOnlyDictionary<AmqpSymbol, object?>? Properties)
{
    /// <summary>
    /// Reads the settings from session options, or the defaults when there are none.
    /// </summary>
    public static AmqpSessionSettings From(AmqpSessionOptions? options)
    {
        options ??= new AmqpSessionOptions();

        return new AmqpSessionSettings(
            options.IncomingWindow,
            options.HandleMax,
            options.OfferedCapabilities,
            options.DesiredCapabilities,
            options.Properties);
    }
}
