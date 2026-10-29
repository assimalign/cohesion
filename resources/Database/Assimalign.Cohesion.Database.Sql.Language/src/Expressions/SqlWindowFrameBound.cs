namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>One inclusive boundary of a window frame.</summary>
public sealed class SqlWindowFrameBound
{
    internal SqlWindowFrameBound(SqlWindowFrameBoundKind kind, SqlExpression? offset = null)
    {
        Kind = kind;
        Offset = offset;
    }

    /// <summary>Gets the boundary's position relative to the current row.</summary>
    public SqlWindowFrameBoundKind Kind { get; }
    /// <summary>Gets the row count for an offset boundary.</summary>
    public SqlExpression? Offset { get; }
}

/// <summary>The position of a frame boundary, in SQL ordering.</summary>
public enum SqlWindowFrameBoundKind
{
    /// <summary>The first row of the partition.</summary>
    UnboundedPreceding,
    /// <summary>An offset before the current row.</summary>
    Preceding,
    /// <summary>The current row, or its peer boundary in RANGE mode.</summary>
    CurrentRow,
    /// <summary>An offset after the current row.</summary>
    Following,
    /// <summary>The last row of the partition.</summary>
    UnboundedFollowing,
}
