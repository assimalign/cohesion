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
    private readonly SqlCatalogColumn?[] _domainParameters;

    internal SqlExpressionFunction(string name, SqlType[] parameters, SqlType returnType,
        SqlFunctionVolatility volatility, bool strict, SqlBoundExpression body, string? unresolved = null, bool notNullCheck = false)
        : base(name, parameters, returnType, volatility,
            strict ? SqlNullBehavior.ReturnsNullOnNullInput : SqlNullBehavior.CalledOnNullInput)
    {
        _body = body;
        _result = new SqlCatalogColumn(name, returnType.Storage);
        _notNullCheck = notNullCheck;
        Unresolved = unresolved;
        _domainParameters = new SqlCatalogColumn?[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].IsDomain)
            {
                _domainParameters[index] = new SqlCatalogColumn(parameters[index].Name, parameters[index].Storage);
            }
        }
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
            object? value = arguments[index].ToObject();
            if (_domainParameters[index] is { } column)
            {
                value = SqlPlanExecutor.CoerceForColumn(value, column);
                CheckDomain(Parameters[index], value, arguments.Context);
            }
            frame[index] = value;
        }
        object? result = SqlExpressionEvaluator.ForExecution(null, arguments.Context.CancellationToken).Evaluate(_body, frame);
        result = SqlPlanExecutor.CoerceForColumn(result, _result);
        CheckDomain(ReturnType, result, arguments.Context);
        return SqlValue.FromObject(result);
    }
    private void CheckDomain(SqlType type, object? value, scoped in SqlFunctionContext context)
    {
        if (type.IsDomain && type.Check is { } check)
        {
            var arguments = new SqlArguments([SqlValue.FromObject(value)], context);
            if (check.Invoke(in arguments).ToObject() is false)
            {
                throw new SqlConstraintViolationException($"A value of SQL function '{Name}' violates domain '{type.Name}'.", type.Name, string.Empty, "DOMAIN");
            }
        }
    }
}
