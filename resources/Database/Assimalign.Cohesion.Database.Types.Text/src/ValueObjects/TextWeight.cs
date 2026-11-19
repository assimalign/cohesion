namespace Assimalign.Cohesion.Database.Types.Text;

/// <summary>A PostgreSQL-compatible two-bit lexeme weight, ordered from D to A.</summary>
public enum TextWeight : byte
{
    /// <summary>The default and lowest weight.</summary>
    D = 0,
    /// <summary>Weight C.</summary>
    C = 1,
    /// <summary>Weight B.</summary>
    B = 2,
    /// <summary>The highest weight.</summary>
    A = 3,
}
