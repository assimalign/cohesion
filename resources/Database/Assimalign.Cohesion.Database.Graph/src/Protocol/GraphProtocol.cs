using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>The graph endpoint's immutable protocol family.</summary>
public static class GraphProtocol
{
    /// <summary>Gets the graph family, including the unchanged catalog exchange and path results.</summary>
    public static ProtocolMessageFamily Family { get; } = new("Graph", 5, 6, 7, 8, 9, 64, 65, 66);
}

/// <summary>Message identifiers scoped to graph endpoints.</summary>
public enum GraphProtocolMessageType : byte
{
    /// <summary>A scalar graph statement or catalog SHOW request.</summary>
    Execute = 5,
    /// <summary>Result column names and scalar types.</summary>
    ResultHeader = 6,
    /// <summary>One scalar result tuple.</summary>
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
