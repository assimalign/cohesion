namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The session transaction control operation.</summary>
public enum OqlTransactionCommand
{
    /// <summary>Begins an explicit transaction.</summary>
    Begin,
    /// <summary>Commits the session's explicit transaction.</summary>
    Commit,
    /// <summary>Rolls back the session's explicit transaction.</summary>
    Rollback,
}
