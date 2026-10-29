namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The SQL spellings of a transaction isolation level.</summary>
public enum SqlTransactionIsolationLevel
{
    /// <summary>READ UNCOMMITTED, executed as READ COMMITTED.</summary>
    ReadUncommitted,
    /// <summary>READ COMMITTED, with a fresh snapshot per statement.</summary>
    ReadCommitted,
    /// <summary>REPEATABLE READ, executed with the begin-time snapshot.</summary>
    RepeatableRead,
    /// <summary>SERIALIZABLE, recognized and refused by the executor.</summary>
    Serializable,
}
