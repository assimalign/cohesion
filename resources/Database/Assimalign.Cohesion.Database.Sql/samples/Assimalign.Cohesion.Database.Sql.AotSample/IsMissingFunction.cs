namespace Assimalign.Cohesion.Database.Sql.AotSample;

/// <summary>
/// A hand-written non-strict scalar leaf: <c>is_missing(ANY)</c> is called on NULL input
/// (<see cref="SqlNullBehavior.CalledOnNullInput"/>) and says whether its argument is NULL.
/// </summary>
internal sealed class IsMissingFunction : SqlScalarFunction
{
    public IsMissingFunction()
        : base("is_missing", [SqlType.Any], SqlType.Boolean, SqlFunctionVolatility.Immutable, SqlNullBehavior.CalledOnNullInput)
    {
    }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromBoolean(arguments.IsNull(0));
}
