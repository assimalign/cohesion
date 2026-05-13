using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Adds attributes (trace correlation, machine identity, environment, ...) to every log entry
/// before fan-out.
/// </summary>
/// <remarks>
/// <para>
/// Enrichers run in registration order. Each enricher is handed the entry's current attribute
/// bag and may add keys to it. Existing keys MUST NOT be overwritten by an enricher; the entry
/// author's intent wins.
/// </para>
/// <para>
/// Implementations must be thread-safe. Enrichment runs on the caller's thread; long-running or
/// blocking work belongs elsewhere.
/// </para>
/// </remarks>
public interface ILoggerEnricher
{
    /// <summary>
    /// Add attributes for <paramref name="entry"/>.
    /// </summary>
    /// <param name="entry">The entry being enriched. Required.</param>
    /// <param name="attributes">Mutable attribute bag the enricher may add to. Required.</param>
    void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes);
}
