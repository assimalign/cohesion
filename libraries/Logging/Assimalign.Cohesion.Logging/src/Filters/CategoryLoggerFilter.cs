using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Category-prefix-driven minimum-level filter. Matches an <see cref="ILoggerEntry.Category"/>
/// against a set of (prefix, minimumLevel) rules; the longest matching prefix wins.
/// </summary>
/// <remarks>
/// <para>
/// Use this filter to express the common case "the App.Network family logs at Debug; everything
/// else respects the factory default". For more elaborate selection rules implement
/// <see cref="ILoggerFilter"/> directly.
/// </para>
/// <para>
/// Filter rules are frozen at construction time; mutate the input collection only before
/// constructing the filter.
/// </para>
/// </remarks>
public sealed class CategoryLoggerFilter : ILoggerFilter
{
    private readonly KeyValuePair<string, LogLevel>[] _rules;

    /// <summary>
    /// Initializes a filter with the supplied rules.
    /// </summary>
    /// <param name="rules">Pairs of (case-insensitive category prefix, minimum log level). Required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <see langword="null"/>.</exception>
    public CategoryLoggerFilter(IEnumerable<KeyValuePair<string, LogLevel>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var collected = new List<KeyValuePair<string, LogLevel>>();
        foreach (var pair in rules)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                throw new ArgumentException("Category prefix must be non-empty.", nameof(rules));
            }
            collected.Add(pair);
        }

        _rules = collected.ToArray();
    }

    /// <inheritdoc />
    public bool ShouldLog(ILoggerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Longest matching prefix wins so the most specific rule for a category applies.
        int bestLength = -1;
        LogLevel? bestLevel = null;

        for (int i = 0; i < _rules.Length; i++)
        {
            var rule = _rules[i];
            if (entry.Category.StartsWith(rule.Key, StringComparison.OrdinalIgnoreCase)
                && rule.Key.Length > bestLength)
            {
                bestLength = rule.Key.Length;
                bestLevel = rule.Value;
            }
        }

        // No rule matched the category: defer to the factory minimum (already enforced upstream).
        if (bestLevel is null)
        {
            return true;
        }

        return entry.Level >= bestLevel.Value && entry.Level != LogLevel.None;
    }
}
