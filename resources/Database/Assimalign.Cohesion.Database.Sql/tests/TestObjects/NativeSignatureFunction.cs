namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class NativeSignatureFunction : SqlScalarFunction
{
    private readonly bool _result;

    internal NativeSignatureFunction(SqlType parameter, SqlType? variadic, bool result)
        : base("native_gate", [parameter], SqlType.Boolean, SqlFunctionVolatility.Immutable, variadicParameter: variadic)
    {
        _result = result;
    }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromBoolean(_result);
}
