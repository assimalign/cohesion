using System;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>Immutable identity generation metadata. Counter state is stored separately.</summary>
public sealed class SqlCatalogIdentity
{
    /// <summary>Creates an identity description.</summary>
    /// <param name="isAlways">Whether explicit assignments are forbidden.</param>
    /// <param name="start">The first generated value.</param>
    /// <param name="increment">The nonzero signed increment.</param>
    /// <exception cref="ArgumentOutOfRangeException">The increment is zero.</exception>
    public SqlCatalogIdentity(bool isAlways, long start = 1, long increment = 1)
    {
        ArgumentOutOfRangeException.ThrowIfZero(increment);
        IsAlways = isAlways;
        Start = start;
        Increment = increment;
    }

    // Declaration options travel with this facet until the owned sequence is staged.
    // Persisted options belong to the sequence record.
    [global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]
    internal SqlSequenceOptions? Options { get; init; }

    /// <summary>Gets whether explicit assignments are forbidden.</summary>
    public bool IsAlways { get; }
    /// <summary>Gets the first generated value.</summary>
    public long Start { get; }
    /// <summary>Gets the increment.</summary>
    public long Increment { get; }
}
