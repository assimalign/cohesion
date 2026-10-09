namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A scalar function call bound to the function its signature resolved to, with its argument
/// expressions bound. The evaluator dispatches on <see cref="Function"/> for every row instead of
/// looking the written name up and matching the call's arguments again, which the binder did once.
/// </summary>
/// <remarks>
/// The built-in scalars accept any argument type and apply no implicit conversion, so a bound call
/// carries no argument coercions yet; overload resolution over typed signatures, which introduces
/// them, is phase E2 of <c>DATABASE_ENGINE_EXTENSIBILITY_DESIGN.md</c>. <c>COALESCE</c> is a special
/// form, not a call: it binds as <see cref="SqlBoundCoalesce"/>.
/// </remarks>
internal sealed class SqlBoundCall : SqlBoundExpression
{
    /// <summary>Initializes a bound call.</summary>
    /// <param name="function">The function the call's signature resolved to.</param>
    /// <param name="arguments">The bound arguments, exactly as many as the signature accepted.</param>
    internal SqlBoundCall(SqlBuiltinFunction function, SqlBoundExpression[] arguments)
        : base(SqlBoundExpressionKind.Call)
    {
        Function = function;
        Arguments = arguments;
    }

    /// <summary>Gets the function the call's signature resolved to.</summary>
    internal SqlBuiltinFunction Function { get; }

    /// <summary>Gets the bound arguments.</summary>
    internal SqlBoundExpression[] Arguments { get; }
}
