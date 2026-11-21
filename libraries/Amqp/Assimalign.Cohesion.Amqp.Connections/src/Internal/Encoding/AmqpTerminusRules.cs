using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Checks the values inside a source or target that the model can hold but AMQP 1.0 does not allow: a
/// filter that is not described, and a standard node property of the wrong type (Part 3, sections 3.5.8
/// and 3.5.9).
/// </summary>
/// <remarks>
/// The encoder refuses a terminus that breaks one before writing it. A peer's attach that opens a link
/// with one loses only that link, through <see cref="AmqpAttachRules"/>; anywhere else, such as a source
/// carried in a message, the value is kept as it arrived.
/// </remarks>
internal static class AmqpTerminusRules
{
    /// <summary>
    /// Finds the first value in a terminus that AMQP 1.0 does not allow.
    /// </summary>
    /// <param name="terminus">The terminus's name, <c>source</c> or <c>target</c>, for the description.</param>
    /// <param name="properties">The terminus's dynamic-node-properties, or <see langword="null"/>.</param>
    /// <param name="filters">The source's filter set, or <see langword="null"/>.</param>
    /// <returns>A description of the broken rule, or <see langword="null"/> when the terminus breaks none.</returns>
    public static string? FindViolation(string terminus, IReadOnlyDictionary<AmqpSymbol, object?>? properties, IReadOnlyDictionary<AmqpSymbol, object?>? filters)
    {
        if (filters is not null)
        {
            foreach (KeyValuePair<AmqpSymbol, object?> filter in filters)
            {
                // A filter is a described type that provides the filter archetype, or null for none. No type
                // the library models provides it, so only an otherwise unknown described value can.
                if (filter.Value is not (null or AmqpDescribedValue))
                {
                    return string.Format(CultureInfo.CurrentCulture, ErrorMessages.FilterNotDescribed, filter.Key);
                }
            }
        }

        if (properties is null)
        {
            return null;
        }

        // A null value stands for the property's absence, as a null filter does.
        if (properties.TryGetValue(AmqpNodeProperties.LifetimePolicy, out object? policy)
            && policy is not (null or AmqpLifetimePolicy or AmqpDescribedValue))
        {
            return string.Format(CultureInfo.CurrentCulture, ErrorMessages.LifetimePolicyInvalid, terminus);
        }

        if (properties.TryGetValue(AmqpNodeProperties.SupportedDistributionModes, out object? modes) && !IsSymbolMultiple(modes))
        {
            return string.Format(CultureInfo.CurrentCulture, ErrorMessages.SupportedDistributionModesInvalid, terminus);
        }

        return null;
    }

    // One or more symbols, as a field with multiple="true" carries them: a symbol, or a non-empty array of symbols.
    private static bool IsSymbolMultiple(object? value)
    {
        return value switch
        {
            null => true,
            AmqpSymbol symbol => symbol.Value is not null,
            AmqpSymbol[] symbols => symbols.Length > 0 && System.Array.TrueForAll(symbols, symbol => symbol.Value is not null),
            _ => false
        };
    }
}
