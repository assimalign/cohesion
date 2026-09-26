using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Configuration shape for <see cref="EventSourceLoggerFactoryExtensions"/> forwarding.
/// </summary>
/// <remarks>
/// <para>
/// The options select <em>which</em> event sources are forwarded. They deliberately have no level
/// setting: each selected source is enabled at the most verbose level the target
/// <see cref="ILoggerFactory"/> accepts for that source's category, so the factory's minimum level
/// and <see cref="LoggerFilterRule"/>s are the only place verbosity is configured.
/// </para>
/// <para>
/// Every Cohesion library names its internal event source after its assembly, so the default
/// <see cref="CohesionSourcePrefix"/> selects all of them, and a longer prefix such as
/// <c>Assimalign.Cohesion.Connections</c> selects one family.
/// </para>
/// </remarks>
public sealed class EventSourceForwardingOptions
{
    /// <summary>
    /// The name prefix shared by every Cohesion event source.
    /// </summary>
    public const string CohesionSourcePrefix = "Assimalign.Cohesion.";

    /// <summary>
    /// Event source name prefixes to forward, matched case-insensitively against the start of
    /// <see cref="System.Diagnostics.Tracing.EventSource.Name"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="CohesionSourcePrefix"/>. Add a prefix such as <c>System.Net.Security</c>
    /// to forward a runtime source as well, or clear the list first to forward only the sources named.
    /// </remarks>
    public IList<string> Sources { get; } = new List<string> { CohesionSourcePrefix };
}
