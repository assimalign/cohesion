using System;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A SQL body, bound once, called through the same ABI as native scalar functions.</summary>
internal sealed class SqlExpressionFunction : SqlScalarFunction
{
    private readonly SqlBoundExpression _body;
    private readonly SqlCatalogColumn _result;
    private readonly bool _notNullCheck;

    internal SqlExpressionFunction(string name, SqlType[] parameters, SqlType returnType,
        SqlFunctionVolatility volatility, bool strict, SqlBoundExpression body, string? unresolved = null, bool notNullCheck = false)
        : base(name, parameters, returnType, volatility,
            strict ? SqlNullBehavior.ReturnsNullOnNullInput : SqlNullBehavior.CalledOnNullInput)
    {
        _body = body;
        _result = new SqlCatalogColumn(name, returnType.Storage);
        _notNullCheck = notNullCheck;
        Unresolved = unresolved;
    }

    internal string? Unresolved { get; }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        arguments.Context.CancellationToken.ThrowIfCancellationRequested();
        if (_notNullCheck && arguments.IsNull(0))
        {
            return SqlValue.FromObject(false);
        }
        var frame = new object?[arguments.Count];
        for (int index = 0; index < frame.Length; index++)
        {
            frame[index] = arguments[index].ToObject();
        }
        object? result = SqlExpressionEvaluator.ForExecution(null, arguments.Context.CancellationToken).Evaluate(_body, frame);
        return SqlValue.FromObject(SqlPlanExecutor.CoerceForColumn(result, _result));
    }
}
