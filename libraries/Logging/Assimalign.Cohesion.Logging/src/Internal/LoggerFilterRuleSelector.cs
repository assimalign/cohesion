using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Implements the rule-selection algorithm documented on <see cref="LoggerFilterRule"/>.
/// </summary>
internal static class LoggerFilterRuleSelector
{
    /// <summary>
    /// Selects the single rule that should govern entries flowing to a logger created for the
    /// supplied <paramref name="providerType"/> and <paramref name="category"/>, or
    /// <see langword="null"/> when no rule applies (callers fall back to the factory's global
    /// minimum level).
    /// </summary>
    public static LoggerFilterRule? Select(IReadOnlyList<LoggerFilterRule> rules, Type providerType, string category)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(providerType);
        ArgumentException.ThrowIfNullOrEmpty(category);

        if (rules.Count == 0)
        {
            return null;
        }

        // Step 1: rules targeting the current provider type win; if none, rules without a type.
        List<LoggerFilterRule>? typedRules = null;
        List<LoggerFilterRule>? untypedRules = null;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.ProviderType == providerType)
            {
                (typedRules ??= new List<LoggerFilterRule>()).Add(rule);
            }
            else if (rule.ProviderType is null)
            {
                (untypedRules ??= new List<LoggerFilterRule>()).Add(rule);
            }
        }

        var candidates = typedRules ?? untypedRules;
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        // Step 2: scan for the longest matching category prefix among the candidates.
        int bestLength = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Category is { } cat
                && category.StartsWith(cat, StringComparison.OrdinalIgnoreCase)
                && cat.Length > bestLength)
            {
                bestLength = cat.Length;
            }
        }

        LoggerFilterRule? selected = null;
        int selectedCount = 0;
        if (bestLength >= 0)
        {
            // Steps 4 + 5: keep the last rule that matches the longest prefix (registration order).
            for (int i = 0; i < candidates.Count; i++)
            {
                var rule = candidates[i];
                if (rule.Category is { } cat
                    && cat.Length == bestLength
                    && category.StartsWith(cat, StringComparison.OrdinalIgnoreCase))
                {
                    selected = rule;
                    selectedCount++;
                }
            }
        }
        else
        {
            // Step 3 + 5: no category match anywhere; fall back to the last rule without a category.
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Category is null)
                {
                    selected = candidates[i];
                    selectedCount++;
                }
            }
        }

        // Step 6 is handled by the caller (fall back to options.MinimumLevel) when this returns null.
        return selectedCount > 0 ? selected : null;
    }
}
