using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A SQL body, bound once per input collation, called through the same ABI as native scalar functions.</summary>
internal sealed class SqlExpressionFunction : SqlScalarFunction
{
    private readonly SqlBoundExpression _body;
    private readonly Collation? _bodyCollation;
    private readonly Func<Collation, CancellationToken, SqlBoundExpression>? _bindBody;
    private readonly ConcurrentDictionary<Collation, SqlBoundExpression> _bodies = new();
    private readonly object _bodySync = new();
    private readonly SqlCatalogColumn _result;
    private readonly DatabaseType _resultCheckTarget;
    private readonly bool _notNullCheck;

    internal SqlExpressionFunction(string name, SqlType[] parameters, SqlType returnType,
        SqlFunctionVolatility volatility, bool strict, SqlBoundExpression body, string? unresolved = null, bool notNullCheck = false,
        Collation? bodyCollation = null, Func<Collation, CancellationToken, SqlBoundExpression>? bindBody = null)
        : base(name, parameters, returnType, volatility,
            strict ? SqlNullBehavior.ReturnsNullOnNullInput : SqlNullBehavior.CalledOnNullInput)
    {
        _body = body;
        _bodyCollation = bodyCollation;
        _bindBody = bindBody;
        _result = new SqlCatalogColumn(name, returnType.Storage);
        _resultCheckTarget = returnType.Check is { } check && SqlFunctionResolver.CoercionTargets(check, 1) is { } targets
            ? targets[0] : DatabaseType.Null;
        _notNullCheck = notNullCheck;
        Unresolved = unresolved;
    }

    internal string? Unresolved { get; }

    // Fixed call collations prepare this during binding. Deferred collations use the same cache.
    // Serialize cache misses without retaining a caller's token or caching its cancellation:
    // another statement waiting for this collation must bind with its own token after a failure.
    internal SqlBoundExpression PrepareBody(Collation collation, CancellationToken cancellationToken)
    {
        if (ReturnType.Check is SqlExpressionFunction resultCheck)
        {
            resultCheck.PrepareBody(collation, cancellationToken);
        }
        if (_bindBody is null || ReferenceEquals(collation, _bodyCollation))
        {
            return _body;
        }
        if (_bodies.TryGetValue(collation, out var body))
        {
            return body;
        }
        lock (_bodySync)
        {
            if (!_bodies.TryGetValue(collation, out body))
            {
                cancellationToken.ThrowIfCancellationRequested();
                body = _bindBody(collation, cancellationToken);
                _bodies.TryAdd(collation, body);
            }
            return body;
        }
    }

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
        object? result = SqlExpressionEvaluator.ForExecution(null, arguments.Context.CancellationToken)
            .Evaluate(PrepareBody(arguments.Context.Collation, arguments.Context.CancellationToken), frame);
        result = SqlPlanExecutor.CoerceForColumn(result, _result);
        CheckDomain(ReturnType, result, arguments.Context);
        return SqlValue.FromObject(result);
    }
    private void CheckDomain(SqlType type, object? value, scoped in SqlFunctionContext context)
    {
        if (type.IsDomain && type.Check is { } check && (value is not null || check.NullBehavior == SqlNullBehavior.CalledOnNullInput)
            && check.InvokeResolved(value, _resultCheckTarget, context.Database, context.Collation, context.CancellationToken) is false)
        {
            throw new SqlConstraintViolationException($"A value of SQL function '{Name}' violates domain '{type.Name}'.", type.Name, string.Empty, "DOMAIN");
        }
    }
}
