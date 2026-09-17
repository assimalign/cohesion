namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// The clause names a
/// <see cref="Assimalign.Cohesion.Database.Language.QueryLanguageProfile"/> for SQL may enable.
/// </summary>
public static class SqlClauses
{
    /// <summary>A <c>SELECT</c> statement.</summary>
    public const string Select = "SELECT";

    /// <summary>An <c>INSERT</c> statement.</summary>
    public const string Insert = "INSERT";

    /// <summary>An <c>UPDATE</c> statement.</summary>
    public const string Update = "UPDATE";

    /// <summary>A <c>DELETE</c> statement.</summary>
    public const string Delete = "DELETE";

    /// <summary>A <c>CREATE TABLE</c> statement.</summary>
    public const string CreateTable = "CREATE TABLE";

    /// <summary>A <c>CREATE INDEX</c> statement.</summary>
    public const string CreateIndex = "CREATE INDEX";

    /// <summary>An <c>ALTER TABLE</c> statement.</summary>
    public const string AlterTable = "ALTER TABLE";

    /// <summary>A <c>DROP TABLE</c> statement.</summary>
    public const string DropTable = "DROP TABLE";

    /// <summary>A <c>DROP INDEX</c> statement.</summary>
    public const string DropIndex = "DROP INDEX";

    /// <summary>A <c>FROM</c> clause.</summary>
    public const string From = "FROM";

    /// <summary>A <c>JOIN</c> clause.</summary>
    public const string Join = "JOIN";

    /// <summary>A <c>WHERE</c> clause.</summary>
    public const string Where = "WHERE";

    /// <summary>A <c>GROUP BY</c> clause.</summary>
    public const string GroupBy = "GROUP BY";

    /// <summary>A <c>HAVING</c> clause.</summary>
    public const string Having = "HAVING";

    /// <summary>An <c>ORDER BY</c> clause.</summary>
    public const string OrderBy = "ORDER BY";

    /// <summary>A <c>LIMIT</c> clause.</summary>
    public const string Limit = "LIMIT";

    /// <summary>An <c>OFFSET</c> clause.</summary>
    public const string Offset = "OFFSET";

    /// <summary>A <c>VALUES</c> clause.</summary>
    public const string Values = "VALUES";

    /// <summary>A scalar, predicate, or insert-source subquery.</summary>
    public const string Subquery = "SUBQUERY";

    /// <summary>A <c>WITH</c> common-table-expression clause.</summary>
    public const string Cte = "WITH";

    /// <summary>A named <c>WINDOW</c> clause.</summary>
    public const string Window = "WINDOW";

    /// <summary>A <c>UNION</c> set operation.</summary>
    public const string SetOperation = "UNION";

    /// <summary>A <c>CASE</c> expression.</summary>
    public const string Case = "CASE";

    /// <summary>A <c>CAST</c> expression.</summary>
    public const string Cast = "CAST";

    /// <summary>A recursive common-table expression.</summary>
    public const string Recursive = "RECURSIVE";

    /// <summary>An <c>INTERSECT</c> set operation.</summary>
    public const string Intersect = "INTERSECT";

    /// <summary>An <c>EXCEPT</c> set operation.</summary>
    public const string Except = "EXCEPT";

    /// <summary>A standard <c>FETCH</c> row-limit clause.</summary>
    public const string Fetch = "FETCH";

    /// <summary>An <c>OVER</c> window specification.</summary>
    public const string Over = "OVER";

    /// <summary>A <c>PARTITION BY</c> window clause.</summary>
    public const string Partition = "PARTITION";

    /// <summary>A DML <c>RETURNING</c> clause.</summary>
    public const string Returning = "RETURNING";

    /// <summary>A <c>TOP</c> row-limit modifier.</summary>
    public const string Top = "TOP";

    /// <summary>An <c>ALL</c> select modifier.</summary>
    public const string All = "ALL";

    /// <summary>A <c>NATURAL</c> join modifier.</summary>
    public const string Natural = "NATURAL";

    /// <summary>A join <c>USING</c> clause.</summary>
    public const string Using = "USING";

    /// <summary>A <c>CREATE VIEW</c> statement.</summary>
    public const string CreateView = "CREATE VIEW";

    /// <summary>A <c>DROP VIEW</c> statement.</summary>
    public const string DropView = "DROP VIEW";

    /// <summary>A named table <c>CONSTRAINT</c>.</summary>
    public const string Constraint = "CONSTRAINT";

    /// <summary>A <c>FOREIGN KEY</c> table constraint.</summary>
    public const string ForeignKey = "FOREIGN KEY";

    /// <summary>A <c>REFERENCES</c> constraint.</summary>
    public const string References = "REFERENCES";

    /// <summary>A <c>CHECK</c> constraint.</summary>
    public const string Check = "CHECK";

    /// <summary>A column or table <c>UNIQUE</c> constraint.</summary>
    public const string UniqueConstraint = "UNIQUE";

    /// <summary>A referential <c>CASCADE</c> action.</summary>
    public const string Cascade = "CASCADE";

    /// <summary>A referential <c>RESTRICT</c> action.</summary>
    public const string Restrict = "RESTRICT";

    /// <summary>A <c>BEGIN</c> transaction-control statement.</summary>
    public const string Begin = "BEGIN";

    /// <summary>A <c>COMMIT</c> transaction-control statement.</summary>
    public const string Commit = "COMMIT";

    /// <summary>A <c>ROLLBACK</c> transaction-control statement.</summary>
    public const string Rollback = "ROLLBACK";

    /// <summary>A standalone <c>TRANSACTION</c> control statement.</summary>
    public const string Transaction = "TRANSACTION";
}
