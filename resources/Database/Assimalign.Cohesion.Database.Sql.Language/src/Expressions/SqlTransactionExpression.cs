using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents BEGIN, COMMIT, ROLLBACK, or DISCARD ALL session control.</summary>
public sealed class SqlTransactionExpression : SqlQueryExpression
{
    internal SqlTransactionExpression(SqlQueryCommandType commandType, Location location)
        : base(commandType, null, location)
    {
    }
}
