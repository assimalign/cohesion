using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentBuiltinAggregateFunction {Name}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentBuiltinAggregateFunction.DebugView))]
internal sealed class DocumentBuiltinAggregateFunction : DocumentAggregateFunction
{
    internal DocumentBuiltinAggregateFunction(OqlFunction definition, int count)
        : base(definition.Name, count, definition.ResultType,
            count == 0 || definition.Name == "ARRAY_AGG" ? DocumentNullBehavior.CalledOnNullOrMissingInput : DocumentNullBehavior.PropagateMissingOrNull) { }

    protected override DocumentAggregateAccumulator CreateAccumulatorCore(scoped in DocumentFunctionContext context)
        => new Accumulator(Name.ToString(), context.CancellationToken);

    internal DocumentAggregateAccumulator CreateAccumulator(scoped in DocumentFunctionContext context,
        DocumentAggregateMemory memory, string? writeVerb, int maxDepth, string resultClause)
    {
        var accumulator = (Accumulator)base.CreateAccumulator(in context);
        accumulator.Configure(memory, writeVerb, maxDepth, resultClause);
        return accumulator;
    }

    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(Accumulator.DebugView))]
    private sealed class Accumulator : DocumentAggregateAccumulator
    {
        private readonly string _name;
        private readonly CancellationToken _cancellationToken;
        private long _count;
        private decimal _sum;
        private object? _extreme;
        private bool _boolean;
        private DocumentSampleStatistics? _statistics;
        private List<object?>? _array;
        private DocumentAggregateMemory? _memory;
        private string? _writeVerb;
        private int _maxDepth = 256;
        private string _resultClause = "SELECT";

        internal Accumulator(string name, CancellationToken cancellationToken)
        {
            _name = name;
            _cancellationToken = cancellationToken;
            _boolean = name != "BOOL_OR";
        }

        internal void Configure(DocumentAggregateMemory memory, string? writeVerb, int maxDepth, string resultClause)
        {
            _memory = memory;
            _writeVerb = writeVerb;
            _maxDepth = maxDepth;
            _resultClause = resultClause;
        }

        protected override void AddCore(scoped in DocumentArguments arguments)
        {
            try
            {
                object? value = arguments.Count == 0 ? null : arguments[0].Value;
                if (_name == "ARRAY_AGG")
                {
                    if (arguments[0].IsMissing) { return; }
                    if (_array is null) { _memory?.Charge(256); _array = []; }
                    _memory?.Retain(value, array: true);
                    _array.Add(value);
                    return;
                }
                if (_name is "BOOL_AND" or "BOOL_OR" or "EVERY")
                {
                    if (value is not bool boolean) { return; }
                    _count = checked(_count + 1);
                    _boolean = _name == "BOOL_OR" ? _boolean || boolean : _boolean && boolean;
                    return;
                }
                if (_name is "STDDEV" or "VARIANCE")
                {
                    if (value is not (decimal or long)) { return; }
                    _count = checked(_count + 1);
                    (_statistics ??= new DocumentSampleStatistics()).Add(DocumentExpressionEvaluator.Number(value));
                    return;
                }
                _count = checked(_count + 1);
                if (arguments.Count == 0 || _name == "COUNT") { return; }
                if (_name is "SUM" or "AVG") { _sum += DocumentExpressionEvaluator.Number(value!); return; }
                if (_count == 1 || (_name == "MIN" ? DocumentExpressionEvaluator.Compare(value, _extreme, _cancellationToken) < 0 :
                    DocumentExpressionEvaluator.Compare(value, _extreme, _cancellationToken) > 0))
                { _extreme = value; }
            }
            catch (OverflowException error)
            { throw new DocumentPlanningException("CD02007", ErrorMessages.ExpressionEvaluatorEvaluateFailure2, innerException: error); }
        }

        protected override DocumentValue FinishCore()
        {
            try
            {
                if (_name == "ARRAY_AGG")
                { return new(_array is null ? null : DocumentJsonValue.Array(Elements(), _writeVerb, _maxDepth, _resultClause)); }
                if (_name == "COUNT") { return new(_count); }
                if (_count == 0) { return DocumentValue.Null; }
                if (_name is "STDDEV" or "VARIANCE")
                {
                    return new(_statistics!.Finish(_name == "STDDEV"));
                }
                return new(_name switch
                {
                    "SUM" => _sum, "AVG" => _sum / _count,
                    "BOOL_AND" or "BOOL_OR" or "EVERY" => _boolean,
                    _ => _extreme,
                });
            }
            catch (OverflowException error)
            { throw new DocumentPlanningException("CD02007", ErrorMessages.ExpressionEvaluatorEvaluateFailure2, innerException: error); }
        }

        private IEnumerable<object?> Elements()
        {
            foreach (var value in _array!) { _cancellationToken.ThrowIfCancellationRequested(); yield return value; }
        }

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
