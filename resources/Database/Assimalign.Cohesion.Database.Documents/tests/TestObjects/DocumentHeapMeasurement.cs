using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Documents.Tests.TestObjects.DocumentHeapPool;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal static class DocumentHeapMeasurement
{
    internal static async Task<T> MeasureAsync<T>(PoolEvents events, ITestOutputHelper output, string test, FormattableString labels,
        Func<Func<long>, Task<T>> measure, CancellationToken cancellationToken = default)
    {
        var gate = new TrimGate(events, output, test, labels);
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var measured = await MeasureAttemptAsync(gate, attempt, measure);
            if (measured.Accepted) { return measured.Result; }
        }
    }

    // Return from each attempt before starting another baseline. A rejected
    // result can own a retained graph; it must not survive on the measuring frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(bool Accepted, T Result)> MeasureAttemptAsync<T>(TrimGate gate, int attempt, Func<Func<long>, Task<T>> measure)
    {
        gate.BeginAttempt();
        T result = await measure(gate.Read);
        return gate.Accept(attempt) ? (true, result) : default;
    }

    private sealed class TrimGate
    {
        private readonly PoolEvents _events;
        private readonly ITestOutputHelper _output;
        private readonly string _test;
        private readonly FormattableString _labels;
        private readonly long[] _trims = new long[4];
        private long _before, _after;
        private bool _started;

        internal TrimGate(PoolEvents events, ITestOutputHelper output, string test, FormattableString labels)
        {
            _events = events;
            _output = output;
            // Eager interpolation rents a char buffer that can itself trim during
            // a long execution. Keep fixture labels unformatted until rejection.
            _test = test;
            _labels = labels;
        }

        internal void BeginAttempt()
        {
            _started = false;
            _before = _after = 0;
        }

        internal long Read()
        {
            if (!_started)
            {
                _started = true;
                _before = Volatile.Read(ref _events.TrimEvents);
            }
            long live = LiveHeap.Read();
            long count = Volatile.Read(ref _events.TrimEvents), previous;
            do { previous = Volatile.Read(ref _after); }
            while (previous < count && Interlocked.CompareExchange(ref _after, count, previous) != previous);
            return live;
        }

        internal bool Accept(int attempt)
        {
            // LiveHeap.Read waits for pending finalizers. A trim that freed a buffer
            // before a reading has therefore delivered its event before that read returns.
            long trims = Volatile.Read(ref _after) - _before;
            if (trims == 0) { return true; }
            _trims[attempt - 1] = trims;
            string execution = string.Concat("test=", _test, "|", _labels.ToString(CultureInfo.CurrentCulture));
            _output.WriteLine(string.Concat("POOL_TRIM_REMEASURE|", execution,
                "|attempt=", attempt.ToString(CultureInfo.CurrentCulture), "|trims=", trims.ToString(CultureInfo.CurrentCulture)));
            if (attempt == _trims.Length)
            {
                trims.ShouldBe(0, $"Pool trims invalidated all 4 attempts of {execution}; attempt trim counts: {string.Join(", ", _trims)}");
            }
            return false;
        }
    }
}
