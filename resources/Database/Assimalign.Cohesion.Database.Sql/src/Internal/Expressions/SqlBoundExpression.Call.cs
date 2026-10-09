using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A scalar function call bound to the overload it resolved to, with its argument expressions
/// bound: the evaluator calls <see cref="Function"/> for every row instead of looking the written
/// name up and resolving the call again, which the binder did once (PostgreSQL's
/// <c>ExecInitFunc</c>, <c>src/backend/executor/execExpr.c</c>).
/// </summary>
/// <remarks>
/// The node also carries what each call needs and the binder already knew: the storage type each
/// argument converts to before the call (<see cref="Targets"/>), the collation the call's input
/// compares under, and the database whose statement makes the call, for the function's
/// <see cref="SqlFunctionContext"/>. <c>COALESCE</c> is a special form, not a call: it binds as
/// <see cref="SqlBoundCoalesce"/>.
/// </remarks>
internal sealed class SqlBoundCall : SqlBoundExpression
{
    /// <summary>Initializes a bound call.</summary>
    /// <param name="function">The overload the call resolved to.</param>
    /// <param name="arguments">The bound arguments, as many as the overload accepts.</param>
    /// <param name="targets">The storage type each argument converts to, or null when none converts.</param>
    /// <param name="collation">The collation the call's input compares under.</param>
    /// <param name="database">The database whose statement makes the call.</param>
    internal SqlBoundCall(SqlScalarFunction function, SqlBoundExpression[] arguments, DatabaseType[]? targets,
        SqlBoundCollation collation, DatabaseName database)
        : base(SqlBoundExpressionKind.Call)
    {
        Function = function;
        Arguments = arguments;
        Targets = targets;
        Collation = collation;
        Database = database;
    }

    /// <summary>Gets the overload the call resolved to.</summary>
    internal SqlScalarFunction Function { get; }

    /// <summary>Gets the bound arguments.</summary>
    internal SqlBoundExpression[] Arguments { get; }

    /// <summary>
    /// Gets the storage type each argument converts to before the call
    /// (<see cref="DatabaseType.Null"/> for a pseudo-type parameter, which takes the value as it is),
    /// or null when every parameter is a pseudo-type, as for every standard-library scalar.
    /// </summary>
    internal DatabaseType[]? Targets { get; }

    /// <summary>Gets the collation the call's input compares under.</summary>
    internal SqlBoundCollation Collation { get; }

    /// <summary>Gets the database whose statement makes the call.</summary>
    internal DatabaseName Database { get; }
}
