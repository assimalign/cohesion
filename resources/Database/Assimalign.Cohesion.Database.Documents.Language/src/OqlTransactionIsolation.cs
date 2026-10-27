namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The isolation level requested by an OQL transaction start.</summary>
public enum OqlTransactionIsolation
{
    /// <summary>Uses one snapshot for the transaction; the Documents default.</summary>
    Snapshot,
    /// <summary>Uses a fresh committed snapshot for each statement.</summary>
    ReadCommitted,
}
