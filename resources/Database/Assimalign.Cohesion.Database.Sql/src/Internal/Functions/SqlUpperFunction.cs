using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>UPPER(TEXT)</c>: invariant upper case; CHAR converts to TEXT before the call.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlUpperFunction {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlUpperFunction.DebugView))]
internal sealed class SqlUpperFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlUpperFunction()
        : base("UPPER", [SqlType.Text], SqlType.Text, SqlFunctionVolatility.Immutable)
    {
        Usage = "UPPER(TEXT)";
        ResultRule = SqlResultRule.TextElement;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        return SqlBuiltinScalarFunction.ChangeCase(in arguments, upper: true);
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlUpperFunction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlUpperFunction value)
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
