namespace Assimalign.Cohesion.Database.Graph.Protocol;

/// <summary>Message identifiers scoped to graph endpoints.</summary>
public enum GraphProtocolMessageType : byte
{
    /// <summary>A graph value statement or catalog SHOW request.</summary>
    Execute = 5,
    /// <summary>Result column names and scalar types.</summary>
    ResultHeader = 6,
    /// <summary>One result tuple using the session's negotiated value encoding.</summary>
    ResultRow = 7,
    /// <summary>Statement exchange completion and affected count.</summary>
    ResultComplete = 8,
    /// <summary>Reserved legacy transaction-control identifier.</summary>
    Transaction = 9,
    /// <summary>A path request using the GQL execute payload.</summary>
    ExecutePaths = 64,
    /// <summary>One ordered path with complete nodes and directed relationships.</summary>
    Path = 65,
    /// <summary>Successful end of a path sequence, carrying its path count.</summary>
    PathsComplete = 66,
}
