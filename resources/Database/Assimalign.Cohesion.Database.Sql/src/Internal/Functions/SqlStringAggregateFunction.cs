using System.Text;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>PostgreSQL text aggregation, also used by ISO LISTAGG.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlStringAggregateFunction {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStringAggregateFunction.DebugView))]
internal sealed class SqlStringAggregateFunction : SqlAggregateFunction
{
    internal SqlStringAggregateFunction(string name, bool delimiter)
        : base(name, delimiter ? [SqlType.Text, SqlType.Text] : [SqlType.Text], SqlType.Text,
            nullBehavior: SqlNullBehavior.CalledOnNullInput)
    {
        Usage = name + (delimiter ? "(value, delimiter) (delimiter must be a string constant or parameter)" : "(value) WITHIN GROUP (ORDER BY key)");
    }

    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStringAggregateFunction.Accumulator.DebugView))]
    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private StringBuilder? _text;

        // PostgreSQL varlena.c:string_agg_transfn skips NULL values and omits NULL delimiters.
        protected override void AddCore(scoped in SqlArguments arguments)
        {
            if (arguments[0].IsNull) { return; }
            string value = (string)arguments[0].ToObject()!;
            string? delimiter = arguments.Count > 1 ? (string?)arguments[1].ToObject() : null;
            SqlBuiltinScalarFunction.CheckLength((long)(_text?.Length ?? 0) + value.Length + (_text is null ? 0 : delimiter?.Length ?? 0));
            if (_text is null) { _text = new StringBuilder(); }
            else { _text.Append(delimiter); }
            _text.Append(value);
        }

        protected override SqlValue FinishCore() => SqlValue.FromObject(_text?.ToString());

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStringAggregateFunction.Accumulator value)
            {
                _ = value;
            }
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlStringAggregateFunction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStringAggregateFunction value)
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
