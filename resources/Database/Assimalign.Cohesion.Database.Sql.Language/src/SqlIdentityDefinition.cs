namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Describes a column's identity generation mode, start and increment.</summary>
public sealed class SqlIdentityDefinition
{
    internal SqlIdentityDefinition(bool isAlways, long start, long increment)
    {
        IsAlways = isAlways;
        Start = start;
        Increment = increment;
    }

    /// <summary>Gets whether explicit assignments are forbidden (GENERATED ALWAYS).</summary>
    public bool IsAlways { get; }
    /// <summary>Gets the first generated value.</summary>
    public long Start { get; }
    /// <summary>Gets the signed increment between generated values.</summary>
    public long Increment { get; }
}
