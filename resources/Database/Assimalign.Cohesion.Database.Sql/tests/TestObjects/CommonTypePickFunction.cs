namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>A strict polymorphic application function returning its first argument.</summary>
internal sealed class CommonTypePickFunction : SqlScalarFunction
{
    internal CommonTypePickFunction()
        : base("pick", [SqlType.AnyElement, SqlType.BigInt], SqlType.AnyElement, SqlFunctionVolatility.Immutable)
    {
    }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => arguments[0];
}
