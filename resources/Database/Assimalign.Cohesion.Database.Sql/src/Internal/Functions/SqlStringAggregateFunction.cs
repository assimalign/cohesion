using System;
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

    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
        => new Accumulator();

    internal SqlAggregateAccumulator CreateWindowAccumulator(SqlWindowMemory memory)
    {
        // Known native leaf: a fresh private accumulator, with no application creation hook.
        var accumulator = new WindowAccumulator(memory);
        accumulator.TryAttach(this);
        return accumulator;
    }

    internal static void ReleaseWindowState(SqlAggregateAccumulator accumulator)
    { if (accumulator is WindowAccumulator state) { state.ReleaseWindowState(); } }

    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStringAggregateFunction.Accumulator.DebugView))]
    private class Accumulator : SqlAggregateAccumulator
    {
        private StringBuilder? _text;

        protected int TextLength => _text?.Length ?? 0;
        protected int TextCapacity => _text?.Capacity ?? 0;
        protected bool HasText => _text is not null;
        protected void ClearText() => _text = null;

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

    // Only window execution adds ownership fields; ordinary aggregates keep their
    // original instance layout. The base's debugger display/proxy is inherited.
    private sealed class WindowAccumulator : Accumulator
    {
        private readonly SqlWindowMemory _memory;
        private long _bytes;

        internal WindowAccumulator(SqlWindowMemory memory) { _memory = memory; }

        protected override void AddCore(scoped in SqlArguments arguments)
        {
            if (arguments[0].IsNull) { return; }
            string value = (string)arguments[0].ToObject()!;
            string? delimiter = arguments.Count > 1 ? (string?)arguments[1].ToObject() : null;
            SqlBuiltinScalarFunction.CheckLength((long)TextLength + value.Length + (HasText ? delimiter?.Length ?? 0 : 0));
            int length = TextLength, capacity = TextCapacity;
            if (!HasText) { Reserve(104); capacity = 16; }
            else { AdmitAppend(delimiter?.Length ?? 0); }
            AdmitAppend(value.Length);
            base.AddCore(in arguments);

            void AdmitAppend(int count)
            {
                int available = capacity - length;
                if (count > available)
                {
                    // ExpandByABlock keeps the old chunk plus a builder node and char array.
                    int size = Math.Max(count - available, Math.Min(capacity, 8000));
                    Reserve(48L + ((24L + 2L * size + 7) & ~7L));
                    capacity += size;
                }
                length += count;
            }
        }

        private void Reserve(long bytes) { _memory.Reserve(bytes); _bytes += bytes; }
        internal void ReleaseWindowState() { _memory.Release(_bytes); _bytes = 0; ClearText(); }
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
