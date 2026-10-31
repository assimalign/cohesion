using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A SQL body, bound once per input collation, called through the same ABI as native scalar functions.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlExpressionFunction {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExpressionFunction.DebugView))]
internal sealed class SqlExpressionFunction : SqlScalarFunction
{
    private readonly SqlBoundExpression? _body;
    private readonly SqlBindingEntry<SqlBoundExpression>? _defaultBody;
    private readonly Collation? _bodyCollation;
    private readonly Func<Collation, CancellationToken, SqlBoundExpression>? _bindBody;
    private readonly ConcurrentDictionary<Collation, SqlBindingEntry<SqlBoundExpression>> _bodies = new();
    private readonly SqlCatalogColumn _result;
    private readonly DatabaseType _resultCheckTarget;
    private readonly bool _notNullCheck;

    internal SqlExpressionFunction(string name, SqlType[] parameters, SqlType returnType,
        SqlFunctionVolatility volatility, bool strict, SqlBoundExpression? body, string? unresolved = null, bool notNullCheck = false,
        Collation? bodyCollation = null, Func<Collation, CancellationToken, SqlBoundExpression>? bindBody = null)
        : base(name, parameters, returnType, volatility,
            strict ? SqlNullBehavior.ReturnsNullOnNullInput : SqlNullBehavior.CalledOnNullInput)
    {
        _body = body;
        _bodyCollation = bodyCollation;
        _bindBody = bindBody;
        if (body is null)
        {
            _defaultBody = new SqlBindingEntry<SqlBoundExpression>(token => bindBody!(bodyCollation!, token));
        }
        _result = new SqlCatalogColumn(name, returnType.Storage);
        _resultCheckTarget = returnType.Check is { } check && SqlFunctionResolver.CoercionTargets(check, 1) is { } targets
            ? targets[0] : DatabaseType.Null;
        _notNullCheck = notNullCheck;
        Unresolved = unresolved;
    }

    internal string? Unresolved { get; }

    // Scope discovery must never compile a second collation or wait for a callback.
    internal SqlBoundExpression? PreparedBody(Collation collation)
        => _bindBody is null || ReferenceEquals(collation, _bodyCollation)
            ? _body ?? _defaultBody!.Value
            : _bodies.TryGetValue(collation, out var entry) ? entry.Value : null;

    // Fixed call collations prepare this during binding. Deferred collations use the same cache.
    // Coordinate only this collation. Other variants never wait on its application callbacks.
    internal SqlBoundExpression PrepareBody(Collation collation, CancellationToken cancellationToken)
    {
        if (ReturnType.Check is SqlExpressionFunction resultCheck)
        {
            resultCheck.PrepareBody(collation, cancellationToken);
        }
        if (_bindBody is null || ReferenceEquals(collation, _bodyCollation))
        {
            return _body ?? _defaultBody!.Get(cancellationToken);
        }
        if (_bodies.TryGetValue(collation, out var body))
        {
            return body.Get(cancellationToken);
        }
        return _bodies.GetOrAdd(collation, key => new SqlBindingEntry<SqlBoundExpression>(token => _bindBody(key, token)))
            .Get(cancellationToken);
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
        object? result;
        try
        {
            result = SqlExpressionEvaluator.ForExecution(null, arguments.Context.CancellationToken, transactionStart: arguments.Context.TransactionStart)
                .Evaluate(PrepareBody(arguments.Context.Collation, arguments.Context.CancellationToken), frame);
        }
        catch (DatabaseException failure) { throw SqlExecutionException.FromPersisted(failure); }
        result = SqlPlanExecutor.CoerceForColumn(result, _result);
        CheckDomain(ReturnType, result, arguments.Context);
        return SqlValue.FromObject(result);
    }
    private void CheckDomain(SqlType type, object? value, scoped in SqlFunctionContext context)
    {
        if (type.IsDomain && type.Check is { } check && (value is not null || check.NullBehavior == SqlNullBehavior.CalledOnNullInput)
            && check.InvokeResolved(value, _resultCheckTarget, context.Database, context.Collation, context.CancellationToken) is false)
        {
            throw new SqlConstraintViolationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionResultDomainViolation, Name, type.Name),
                type.Name, string.Empty, "DOMAIN");
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlExpressionFunction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExpressionFunction value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Sql.SqlFunctionKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.Database.Sql.SqlType? VariadicParameter => _target.VariadicParameter;
        public global::Assimalign.Cohesion.Database.Sql.SqlType ReturnType => _target.ReturnType;
        public global::Assimalign.Cohesion.Database.Sql.SqlFunctionVolatility Volatility => _target.Volatility;
        public global::Assimalign.Cohesion.Database.Sql.SqlNullBehavior NullBehavior => _target.NullBehavior;
        public bool IsNeverNull => _target.IsNeverNull;
    }
}
