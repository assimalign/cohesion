using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

internal sealed class DocumentReusingAggregateFunction : DocumentAggregateFunction
{
    private readonly Accumulator _accumulator = new();
    internal DocumentReusingAggregateFunction() : base("reuse_group", 1, DatabaseType.Int64) { }
    protected override DocumentAggregateAccumulator CreateAccumulatorCore(scoped in DocumentFunctionContext context) => _accumulator;

    private sealed class Accumulator : DocumentAggregateAccumulator
    {
        private long _count;
        protected override void AddCore(scoped in DocumentArguments arguments) { _count++; }
        protected override DocumentValue FinishCore() => DocumentValue.FromInt64(_count);
    }
}
