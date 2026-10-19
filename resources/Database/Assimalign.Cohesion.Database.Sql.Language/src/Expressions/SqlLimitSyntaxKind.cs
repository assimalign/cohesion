namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Identifies the syntax that supplies a SELECT row limit.</summary>
public enum SqlLimitSyntaxKind
{
    /// <summary>The PostgreSQL LIMIT clause.</summary>
    Limit,

    /// <summary>The ISO FETCH FIRST or FETCH NEXT clause.</summary>
    Fetch,

    /// <summary>The SQL Server TOP lexical alias.</summary>
    Top,
}
