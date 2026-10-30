namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// The clause names a
/// <see cref="Assimalign.Cohesion.Database.Language.QueryLanguageProfile"/> for SQL may enable.
/// </summary>
public static class SqlClauses
{
    /// <summary>A <c>SELECT</c> statement.</summary>
    public const string Select = "SELECT";

    /// <summary>An <c>EXPLAIN</c> description of a data statement.</summary>
    public const string Explain = "EXPLAIN";

    /// <summary>An <c>INSERT</c> statement.</summary>
    public const string Insert = "INSERT";

    /// <summary>An INSERT's unique-key conflict action.</summary>
    public const string OnConflict = "ON CONFLICT";

    /// <summary>A MERGE statement whose syntax and binding are supported.</summary>
    public const string Merge = "MERGE";

    /// <summary>An <c>UPDATE</c> statement.</summary>
    public const string Update = "UPDATE";

    /// <summary>An UPDATE with joined read sources.</summary>
    public const string UpdateFrom = "UPDATE FROM";

    /// <summary>A <c>DELETE</c> statement.</summary>
    public const string Delete = "DELETE";

    /// <summary>A DELETE with joined read sources.</summary>
    public const string DeleteUsing = "DELETE USING";

    /// <summary>A <c>CREATE TABLE</c> statement.</summary>
    public const string CreateTable = "CREATE TABLE";

    /// <summary>CREATE TABLE AS with inferred output columns and optional data.</summary>
    public const string CreateTableAs = "CREATE TABLE AS";

    /// <summary>A <c>CREATE SCHEMA</c> SQL namespace definition.</summary>
    public const string CreateSchema = "CREATE SCHEMA";

    /// <summary>Creates a standalone sequence.</summary>
    public const string CreateSequence = "CREATE SEQUENCE";

    /// <summary>Changes or restarts a standalone sequence.</summary>
    public const string AlterSequence = "ALTER SEQUENCE";

    /// <summary>Drops standalone sequences.</summary>
    public const string DropSequence = "DROP SEQUENCE";

    /// <summary>Reads a standard per-row sequence value.</summary>
    public const string NextValue = "NEXT VALUE FOR";

    /// <summary>Discard the logical SQL session's state outside a transaction.</summary>
    public const string DiscardAll = "DISCARD ALL";

    /// <summary>A generated identity column, including the SQL Server lexical alias.</summary>
    public const string Identity = "IDENTITY";

    /// <summary>Changes an existing column identity.</summary>
    public const string AlterIdentity = "ALTER IDENTITY";

    /// <summary>Serial integer identity aliases.</summary>
    public const string Serial = "SERIAL";

    /// <summary>An INSERT identity assignment override.</summary>
    public const string Overriding = "OVERRIDING";

    /// <summary>A sequence column ownership declaration.</summary>
    public const string OwnedBy = "OWNED BY";

    /// <summary>A <c>CREATE INDEX</c> statement.</summary>
    public const string CreateIndex = "CREATE INDEX";

    /// <summary>A partial index with an immutable row predicate.</summary>
    public const string PartialIndex = "CREATE INDEX WHERE";

    /// <summary>An <c>ALTER TABLE</c> statement.</summary>
    public const string AlterTable = "ALTER TABLE";

    /// <summary>A <c>DROP TABLE</c> statement.</summary>
    public const string DropTable = "DROP TABLE";

    /// <summary>Empty one or more tables, with identity and foreign-key policies.</summary>
    public const string Truncate = "TRUNCATE";

    /// <summary>A <c>DROP SCHEMA</c> SQL namespace removal.</summary>
    public const string DropSchema = "DROP SCHEMA";

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

    /// <summary>The GROUPING SETS grouping element.</summary>
    public const string GroupingSets = "GROUPING SETS";

    /// <summary>The ROLLUP grouping element.</summary>
    public const string Rollup = "ROLLUP";

    /// <summary>The CUBE grouping element.</summary>
    public const string Cube = "CUBE";

    /// <summary>An aggregate input FILTER condition.</summary>
    public const string Filter = "FILTER";

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

    /// <summary>A scalar ANY, SOME or ALL comparison over a subquery.</summary>
    public const string QuantifiedComparison = "QUANTIFIED COMPARISON";

    /// <summary>A <c>WITH</c> common-table-expression clause.</summary>
    public const string Cte = "WITH";

    /// <summary>A named <c>WINDOW</c> clause.</summary>
    public const string Window = "WINDOW";

    /// <summary>A physical-row window frame.</summary>
    public const string WindowRows = "ROWS window frame";

    /// <summary>A peer-based window frame without offsets.</summary>
    public const string WindowRange = "RANGE window frame";

    /// <summary>Peer-group window frames.</summary>
    public const string WindowGroups = "GROUPS window frame";

    /// <summary>Window frame exclusion.</summary>
    public const string WindowExclude = "EXCLUDE window frame";

    /// <summary>NULL treatment for navigation and frame value functions.</summary>
    public const string WindowNullTreatment = "window NULL treatment";

    /// <summary>A <c>UNION</c> set operation.</summary>
    public const string SetOperation = "UNION";

    /// <summary>A <c>CASE</c> expression.</summary>
    public const string Case = "CASE";

    /// <summary>A <c>CAST</c> expression.</summary>
    public const string Cast = "CAST";

    /// <summary>A column or expression <c>COLLATE</c> override.</summary>
    public const string Collate = "COLLATE";

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

    /// <summary>A foreign-key <c>ON UPDATE</c> action declaration.</summary>
    public const string OnUpdate = "ON UPDATE";

    /// <summary>A <c>CHECK</c> constraint.</summary>
    public const string Check = "CHECK";

    /// <summary>A column or table <c>UNIQUE</c> constraint.</summary>
    public const string UniqueConstraint = "UNIQUE";

    /// <summary>A referential <c>CASCADE</c> action.</summary>
    public const string Cascade = "CASCADE";

    /// <summary>A referential <c>RESTRICT</c> action.</summary>
    public const string Restrict = "RESTRICT";

    /// <summary>Assigns NULL through a foreign-key action.</summary>
    public const string SetNull = "SET NULL";

    /// <summary>Assigns defaults through a foreign-key action.</summary>
    public const string SetDefault = "SET DEFAULT";

    /// <summary>Checks foreign-key references at statement end.</summary>
    public const string NoAction = "NO ACTION";

    /// <summary>A <c>BEGIN</c> transaction-control statement.</summary>
    public const string Begin = "BEGIN";
    /// <summary>The isolation option of BEGIN and START TRANSACTION.</summary>
    public const string IsolationLevel = "ISOLATION LEVEL";
    /// <summary>Configure the current transaction's isolation level.</summary>
    public const string SetTransaction = "SET TRANSACTION";

    /// <summary>A <c>COMMIT</c> transaction-control statement.</summary>
    public const string Commit = "COMMIT";

    /// <summary>A <c>ROLLBACK</c> transaction-control statement.</summary>
    public const string Rollback = "ROLLBACK";

    /// <summary>The <c>TRANSACTION</c> keyword, required after <c>START</c> and optional (with <c>WORK</c> as a synonym) after <c>BEGIN</c>, <c>COMMIT</c>, or <c>ROLLBACK</c>.</summary>
    public const string Transaction = "TRANSACTION";
}
