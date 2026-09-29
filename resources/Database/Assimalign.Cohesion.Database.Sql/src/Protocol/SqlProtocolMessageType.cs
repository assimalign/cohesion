using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Message identifiers owned by the Sql endpoint.</summary>
public enum SqlProtocolMessageType : byte
{
    /// <summary>Executes a model statement.</summary>
    Execute = 5,
    /// <summary>Describes result columns.</summary>
    ResultHeader = 6,
    /// <summary>Carries one encoded result row.</summary>
    ResultRow = 7,
    /// <summary>Completes a statement.</summary>
    ResultComplete = 8,
    /// <summary>Reserved transaction control identifier.</summary>
    Transaction = 9,
}
