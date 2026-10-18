using System.Text;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>PostgreSQL text aggregation, also used by ISO LISTAGG.</summary>
internal sealed class SqlStringAggregateFunction : SqlAggregateFunction
{
    internal SqlStringAggregateFunction(string name, bool delimiter)
        : base(name, delimiter ? [SqlType.Text, SqlType.Text] : [SqlType.Text], SqlType.Text,
            nullBehavior: SqlNullBehavior.CalledOnNullInput)
    {
        Usage = name + (delimiter ? "(value, delimiter) (delimiter must be a string constant or parameter)" : "(value) WITHIN GROUP (ORDER BY key)");
    }

    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

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
    }
}
