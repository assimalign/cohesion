using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// One rule in a Cohesion logging filter ruleset.
/// </summary>
/// <remarks>
/// <para>
/// A rule pairs an optional provider type, an optional category prefix, an optional minimum
/// log level, and an optional <see cref="ILoggerFilter"/> predicate. Rules are evaluated per
/// (provider, category) pair by <see cref="LoggerFactory"/> at logger creation time, using the
/// selection algorithm in <c>docs/DESIGN.md</c>:
/// </para>
/// <list type="number">
///   <item><description>Rules with a <see cref="ProviderType"/> matching the current logger's provider take priority; if none, rules with <see cref="ProviderType"/> = <see langword="null"/> are considered.</description></item>
///   <item><description>Among those, the rules whose <see cref="Category"/> is the longest matching prefix of the entry's category win.</description></item>
///   <item><description>If no rule matched by category, rules with <see cref="Category"/> = <see langword="null"/> are considered.</description></item>
///   <item><description>If exactly one rule survives, it is used.</description></item>
///   <item><description>If multiple rules survive, the last one registered wins.</description></item>
///   <item><description>If no rule applies, <see cref="LoggerFactoryOptions.MinimumLevel"/> is used as the gate.</description></item>
/// </list>
/// <para>
/// Once a rule is selected for a (provider, category) pair, an entry passes through that
/// provider's fan-out when both:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Level"/> is <see langword="null"/> or <c>entry.Level &gt;= Level</c>.</description></item>
///   <item><description><see cref="Filter"/> is <see langword="null"/> or <see cref="ILoggerFilter.ShouldLog(ILoggerEntry)"/> returns <see langword="true"/>.</description></item>
/// </list>
/// </remarks>
public sealed class LoggerFilterRule
{
    /// <summary>
    /// Initializes a new rule.
    /// </summary>
    /// <param name="providerType">Optional provider type the rule targets. When <see langword="null"/>, the rule applies to every provider that has no type-specific rule.</param>
    /// <param name="category">Optional category prefix (case-insensitive). When <see langword="null"/>, the rule applies to every category in its candidate set.</param>
    /// <param name="level">Optional minimum log level required for this rule to admit an entry.</param>
    /// <param name="filter">Optional per-entry predicate that further screens entries the rule's <paramref name="level"/> already accepts.</param>
    public LoggerFilterRule(
        Type? providerType = null,
        string? category = null,
        LogLevel? level = null,
        ILoggerFilter? filter = null)
    {
        ProviderType = providerType;
        Category = category;
        Level = level;
        Filter = filter;
    }

    /// <summary>The provider type this rule targets, or <see langword="null"/> for "any provider".</summary>
    public Type? ProviderType { get; }

    /// <summary>The category prefix this rule targets, or <see langword="null"/> for "any category".</summary>
    public string? Category { get; }

    /// <summary>Optional minimum level for the rule. When <see langword="null"/>, the rule does not impose a level constraint.</summary>
    public LogLevel? Level { get; }

    /// <summary>Optional entry-level predicate. When <see langword="null"/>, the rule does not impose a filter constraint.</summary>
    public ILoggerFilter? Filter { get; }
}
