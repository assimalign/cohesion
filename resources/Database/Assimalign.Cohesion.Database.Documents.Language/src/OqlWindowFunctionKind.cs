namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The executable ranking and navigation window functions.</summary>
public enum OqlWindowFunctionKind
{
    /// <summary>A scalar or aggregate function.</summary>
    None,
    /// <summary>The one-based partition row number.</summary>
    RowNumber,
    /// <summary>The first peer's one-based position.</summary>
    Rank,
    /// <summary>The one-based peer group number.</summary>
    DenseRank,
    /// <summary>The rank's fraction of the partition excluding the current row.</summary>
    PercentRank,
    /// <summary>The last peer's fraction of the partition.</summary>
    CumeDist,
    /// <summary>The one-based balanced bucket number.</summary>
    Ntile,
    /// <summary>A preceding row's value.</summary>
    Lag,
    /// <summary>A following row's value.</summary>
    Lead,
}
