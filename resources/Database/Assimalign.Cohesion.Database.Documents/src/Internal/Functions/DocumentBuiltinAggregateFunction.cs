using System;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentBuiltinAggregateFunction {Name}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentBuiltinAggregateFunction.DebugView))]
internal sealed class DocumentBuiltinAggregateFunction : DocumentAggregateFunction
{
    internal DocumentBuiltinAggregateFunction(OqlFunction definition, int count)
        : base(definition.Name, count, definition.ResultType, count == 0 ? DocumentNullBehavior.CalledOnNullOrMissingInput : DocumentNullBehavior.PropagateMissingOrNull) { }
    protected override DocumentAggregateAccumulator CreateAccumulatorCore(scoped in DocumentFunctionContext context)
        => new Accumulator(Name.ToString());

    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(Accumulator.DebugView))]
    private sealed class Accumulator : DocumentAggregateAccumulator
    {
        private readonly string _name;
        private long _count;
        private decimal _sum;
        private object? _extreme;
        internal Accumulator(string name) { _name = name; }
        protected override void AddCore(scoped in DocumentArguments arguments)
        {
            try
            {
                _count = checked(_count + 1);
                if (arguments.Count == 0 || _name == "COUNT") { return; }
                object? value = arguments[0].Value;
                if (_name is "SUM" or "AVG") { _sum += DocumentExpressionEvaluator.Number(value!); return; }
                if (_count == 1 || (_name == "MIN" ? DocumentExpressionEvaluator.Compare(value, _extreme) < 0 : DocumentExpressionEvaluator.Compare(value, _extreme) > 0))
                { _extreme = value; }
            }
            catch (OverflowException error)
            { throw new DocumentPlanningException("CD02007", ErrorMessages.ExpressionEvaluatorEvaluateFailure2, innerException: error); }
        }
        protected override DocumentValue FinishCore()
            => new(_name == "COUNT" ? _count : _count == 0 ? null : _name switch
            {
                "SUM" => _sum, "AVG" => _sum / _count, _ => _extreme,
            });

        private sealed class DebugView
        {
            public DebugView(Accumulator value) { _ = value; }
        }
    }
    private sealed class DebugView
    {
        private readonly DocumentBuiltinAggregateFunction _target;
        public DebugView(DocumentBuiltinAggregateFunction value) { _target = value; }
        public DocumentFunctionName Name => _target.Name;
    }

}
