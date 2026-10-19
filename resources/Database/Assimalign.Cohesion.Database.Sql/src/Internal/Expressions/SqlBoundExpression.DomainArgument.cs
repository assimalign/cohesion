using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A function argument's domain coercion, evaluated before the function's STRICT dispatch.</summary>
internal sealed class SqlBoundDomainArgument : SqlBoundExpression
{
    private readonly SqlType _domain;
    private readonly SqlCatalogColumn _column;
    private readonly string _functionName;
    private readonly SqlBoundCollation _collation;
    private readonly DatabaseName _database;
    private readonly DatabaseType _checkTarget;

    internal SqlBoundDomainArgument(SqlBoundExpression operand, SqlType domain, string functionName,
        SqlBoundCollation collation, DatabaseName database, CancellationToken cancellationToken) : base(SqlBoundExpressionKind.DomainArgument)
    {
        Operand = operand;
        _domain = domain;
        _column = new SqlCatalogColumn(domain.Name, domain.Storage);
        _functionName = functionName;
        _collation = collation;
        _database = database;
        _checkTarget = domain.Check is { } check && SqlFunctionResolver.CoercionTargets(check, 1) is { } targets
            ? targets[0] : DatabaseType.Null;
        if (domain.Check is SqlExpressionFunction expression && collation.IsFixed)
        {
            expression.PrepareBody(collation.Resolve(null), cancellationToken);
        }
    }

    internal SqlBoundExpression Operand { get; }

    internal object? Coerce(object? value, SqlSubqueryValues? subqueries, CancellationToken cancellationToken)
    {
        // PostgreSQL builds CoerceToDomain into the argument before strict dispatch:
        // parse_coerce.c:699-723 and execExpr.c:2752-2791. NULL must reach a NOT NULL domain check.
        value = SqlPlanExecutor.CoerceForColumn(value, _column);
        if (_domain.Check is { } check && (value is not null || check.NullBehavior == SqlNullBehavior.CalledOnNullInput)
            && check.InvokeResolved(value, _checkTarget, _database, _collation.Resolve(subqueries), cancellationToken) is false)
        {
            throw new SqlConstraintViolationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionArgumentDomainViolation, _functionName, _domain.Name),
                _domain.Name, string.Empty, "DOMAIN");
        }
        return value;
    }
}
