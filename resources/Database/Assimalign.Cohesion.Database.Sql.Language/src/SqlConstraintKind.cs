namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Identifies a parsed table or column constraint.</summary>
public enum SqlConstraintKind
{
    /// <summary>A primary key.</summary>
    PrimaryKey,
    /// <summary>A reference to another table's key.</summary>
    ForeignKey,
    /// <summary>A row predicate.</summary>
    Check,
    /// <summary>A unique key, lowered to a unique catalog index.</summary>
    Unique,
}
