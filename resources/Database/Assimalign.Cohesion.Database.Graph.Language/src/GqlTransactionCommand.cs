namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The ISO transaction control statement carried by an Execute request.</summary>
public enum GqlTransactionCommand : byte
{
    /// <summary>An ordinary query or catalog statement.</summary>
    None,
    /// <summary>START TRANSACTION, using the session's default isolation level.</summary>
    Start,
    /// <summary>COMMIT the explicit transaction.</summary>
    Commit,
    /// <summary>ROLLBACK the explicit transaction, including a failed transaction.</summary>
    Rollback
}
