using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Describes a column's identity generation mode, start and increment.</summary>
public sealed class SqlIdentityDefinition
{
    internal SqlIdentityDefinition(bool isAlways, long start, long increment, IReadOnlyList<SqlSequenceOption>? options = null)
    {
        IsAlways = isAlways;
        Start = start;
        Increment = increment;
        Options = options ?? [];
    }

    /// <summary>Gets whether explicit assignments are forbidden (GENERATED ALWAYS).</summary>
    public bool IsAlways { get; }
    /// <summary>Gets the first generated value.</summary>
    public long Start { get; }
    /// <summary>Gets the signed increment between generated values.</summary>
    public long Increment { get; }
    /// <summary>Gets explicit sequence options in source order.</summary>
    public IReadOnlyList<SqlSequenceOption> Options { get; }
}
