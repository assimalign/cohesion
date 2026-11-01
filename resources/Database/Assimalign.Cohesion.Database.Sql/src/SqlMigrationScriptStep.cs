using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Pairs one portable migration operation with executable SQL and its compensation.</summary>
public sealed class SqlMigrationScriptStep
{
    internal SqlMigrationScriptStep(
        SqlSchemaMigrationOperation operation,
        string statementText,
        string? rollbackStatementText)
    {
        Operation = operation;
        StatementText = statementText;

        // Parsed at the dialect's ceiling, not the default limit: the engine that executes the
        // step applies its own configured limit to the request (#1151), so a schema written for
        // an engine with a high limit is not cut short here.
        Request = SqlQueryRequest.FromSql(statementText, null, SqlQueryRequest.CeilingParserOptions);
        RollbackStatementText = rollbackStatementText;
        RollbackRequest = rollbackStatementText is null
            ? null
            : SqlQueryRequest.FromSql(rollbackStatementText, null, SqlQueryRequest.CeilingParserOptions);
    }

    /// <summary>Gets the portable operation represented by this step.</summary>
    public SqlSchemaMigrationOperation Operation { get; }

    /// <summary>Gets the deterministic SQL statement text.</summary>
    public string StatementText { get; }

    /// <summary>Gets the parsed SQL request ready for execution.</summary>
    public SqlQueryRequest Request { get; }

    /// <summary>Gets the deterministic compensating SQL statement, when the step is reversible.</summary>
    public string? RollbackStatementText { get; }

    /// <summary>Gets the parsed compensating SQL request, when the step is reversible.</summary>
    public SqlQueryRequest? RollbackRequest { get; }
    // Stable kind-9 identities distinguish the lowered actions of one portable change.
    internal string Kind => Request.Statement.SqlExpression is SqlAlterTableExpression alter ? alter.Action switch
    {
        SqlAlterColumnTypeAction => "AlterColumnType",
        SqlAlterSetDefaultAction => "AlterColumnDefault",
        SqlAlterDropDefaultAction => "DropColumnDefault",
        _ => Operation.Kind.ToString(),
    } : Operation.Kind.ToString();
}
