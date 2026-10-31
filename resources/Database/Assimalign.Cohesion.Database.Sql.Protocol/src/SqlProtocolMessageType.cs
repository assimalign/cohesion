using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Protocol;

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
    /// <summary>Requests a table export using an Execute payload with no parameters.</summary>
    CopyTo = 80,
    /// <summary>Opens a UTF-8 export; the payload is empty.</summary>
    CopyStart = 81,
    /// <summary>Carries up to 16 KiB of uninterpreted export bytes.</summary>
    CopyData = 82,
    /// <summary>Certifies the exported data-row and byte counts.</summary>
    CopyComplete = 83,
    /// <summary>Acknowledges delivery of one CopyData frame; the payload is empty.</summary>
    CopyAcknowledged = 84,
}
