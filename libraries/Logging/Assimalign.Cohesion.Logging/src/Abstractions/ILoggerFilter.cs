namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Decides whether a candidate <see cref="ILoggerEntry"/> reaches the fan-out stage.
/// </summary>
/// <remarks>
/// <para>
/// The filter receives the complete entry - category, level, message, attributes, exception - so
/// it can implement category-prefix matching, sampling, attribute-driven inclusion lists, or any
/// other selection rule the host needs.
/// </para>
/// <para>
/// Filters run after the factory-wide minimum level has already accepted the entry. They are the
/// per-entry override surface. Implementations must be thread-safe; the filter runs on the
/// caller's thread during <see cref="ILogger.Log(ILoggerEntry)"/>.
/// </para>
/// </remarks>
public interface ILoggerFilter
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="entry"/> should be admitted to the
    /// fan-out stage. Returning <see langword="false"/> drops the entry before any provider sees
    /// it.
    /// </summary>
    bool ShouldLog(ILoggerEntry entry);
}
