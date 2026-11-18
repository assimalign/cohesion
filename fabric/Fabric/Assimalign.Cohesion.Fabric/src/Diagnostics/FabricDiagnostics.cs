using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The findings of one validation pass.
/// </summary>
/// <param name="Items">The findings, in the order they were found.</param>
public sealed record FabricDiagnostics(IReadOnlyList<FabricDiagnostic> Items)
{
    /// <summary>
    /// Gets an empty result.
    /// </summary>
    public static FabricDiagnostics None { get; } = new([]);

    /// <summary>
    /// Gets a value indicating whether any finding is an <see cref="FabricDiagnosticSeverity.Error"/>.
    /// </summary>
    public bool HasErrors => Items.Any(item => item.Severity == FabricDiagnosticSeverity.Error);

    /// <summary>
    /// Combines this result with another, keeping both orders.
    /// </summary>
    /// <param name="other">The other result.</param>
    /// <returns>The combined result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="other"/> is <see langword="null"/>.</exception>
    public FabricDiagnostics Combine(FabricDiagnostics other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Items.Count == 0)
        {
            return this;
        }

        return Items.Count == 0 ? other : new FabricDiagnostics([.. Items, .. other.Items]);
    }
}
