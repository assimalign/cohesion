namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>An explicit frame within an ordered window partition.</summary>
public sealed class SqlWindowFrame
{
    internal SqlWindowFrame(SqlWindowFrameMode mode, SqlWindowFrameBound start, SqlWindowFrameBound end, SqlWindowExclusion exclusion = SqlWindowExclusion.NoOthers)
    {
        Mode = mode;
        Start = start;
        End = end;
        Exclusion = exclusion;
    }

    /// <summary>Gets whether boundaries count rows or peers.</summary>
    public SqlWindowFrameMode Mode { get; }
    /// <summary>Gets the inclusive start boundary.</summary>
    public SqlWindowFrameBound Start { get; }
    /// <summary>Gets the members excluded after determining the frame boundaries.</summary>
    public SqlWindowExclusion Exclusion { get; }
    /// <summary>Gets the inclusive end boundary.</summary>
    public SqlWindowFrameBound End { get; }
}

/// <summary>The unit of a window frame.</summary>
public enum SqlWindowFrameMode
{
    /// <summary>Physical rows in the window ordering.</summary>
    Rows,
    /// <summary>Distances in the ordering key domain.</summary>
    Range,
    /// <summary>Peer groups in the window ordering.</summary>
    Groups,
}
