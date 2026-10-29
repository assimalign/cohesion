namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>An explicit frame within an ordered window partition.</summary>
public sealed class SqlWindowFrame
{
    internal SqlWindowFrame(SqlWindowFrameMode mode, SqlWindowFrameBound start, SqlWindowFrameBound end)
    {
        Mode = mode;
        Start = start;
        End = end;
    }

    /// <summary>Gets whether boundaries count rows or peers.</summary>
    public SqlWindowFrameMode Mode { get; }
    /// <summary>Gets the inclusive start boundary.</summary>
    public SqlWindowFrameBound Start { get; }
    /// <summary>Gets the inclusive end boundary.</summary>
    public SqlWindowFrameBound End { get; }
}

/// <summary>The unit of a window frame.</summary>
public enum SqlWindowFrameMode
{
    /// <summary>Physical rows in the window ordering.</summary>
    Rows,
    /// <summary>Ordering peers; offset RANGE frames are deferred.</summary>
    Range,
}
