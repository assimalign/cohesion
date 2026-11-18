using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Documents.Tests.TestObjects.DocumentHeapPool;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal static class DocumentHeapMeasurement
{
    internal static T Measure<T>(PoolEvents events, ITestOutputHelper output, string test, string labels, Func<Func<long>, T> measure)
    {
        var gate = new TrimGate(events, output, test, labels);
        for (int attempt = 1; ; attempt++)
        {
            T result = measure(gate.ReadBaseline);
            if (gate.Accept(attempt)) { return result; }
        }
    }

    internal static async Task<T> MeasureAsync<T>(PoolEvents events, ITestOutputHelper output, string test, string labels,
        Func<Func<long>, Task<T>> measure, CancellationToken cancellationToken = default)
    {
        var gate = new TrimGate(events, output, test, labels);
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            T result = await measure(gate.ReadBaseline);
            if (gate.Accept(attempt)) { return result; }
        }
    }

    private sealed class TrimGate
    {
        private readonly PoolEvents _events;
        private readonly ITestOutputHelper _output;
        private readonly string _execution;
        private readonly long[] _trims = new long[4];
        private long _before;

        internal TrimGate(PoolEvents events, ITestOutputHelper output, string test, string labels)
        {
            _events = events;
            _output = output;
            _execution = $"test={test}|{labels}";
        }

        internal long ReadBaseline()
        {
            _before = Volatile.Read(ref _events.TrimEvents);
            return LiveHeap.Read();
        }

        internal bool Accept(int attempt)
        {
            // LiveHeap.Read waits for pending finalizers. A trim that freed a buffer
            // before a reading has therefore delivered its event before that read returns.
            long trims = Volatile.Read(ref _events.TrimEvents) - _before;
            if (trims == 0) { return true; }
            _trims[attempt - 1] = trims;
            _output.WriteLine($"POOL_TRIM_REMEASURE|{_execution}|attempt={attempt}|trims={trims}");
            if (attempt == _trims.Length)
            {
                trims.ShouldBe(0, $"Pool trims invalidated all 4 attempts of {_execution}; attempt trim counts: {string.Join(", ", _trims)}");
            }
            return false;
        }
    }
}
