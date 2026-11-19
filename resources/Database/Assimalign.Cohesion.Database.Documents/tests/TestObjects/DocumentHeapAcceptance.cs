using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Documents.Tests.TestObjects.DocumentHeapPool;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal static class DocumentHeapAcceptance
{
    internal readonly record struct Term(double Bytes, Execution[] Sources)
    {
        // Plain loops, not LINQ, here and below: LINQ materialization rents its segments from
        // ArrayPool<T>.Shared, and a later trim of those arrays is an unsized trim that forces a remeasure.
        internal static Term Maximum(IEnumerable<Execution> sources)
        {
            var items = new List<Execution>();
            long bytes = 0;
            foreach (Execution source in sources) { items.Add(source); bytes = Math.Max(bytes, source.TrimmedBytes); }
            return new(bytes, items.ToArray());
        }

        public static Term operator +(Term left, Term right)
        {
            var sources = new List<Execution>();
            foreach (Execution source in left.Sources) { if (!sources.Contains(source)) { sources.Add(source); } }
            foreach (Execution source in right.Sources) { if (!sources.Contains(source)) { sources.Add(source); } }
            return new(left.Bytes + right.Bytes, sources.ToArray());
        }
        public static Term operator /(Term value, double divisor) => new(value.Bytes / divisor, value.Sources);
    }

    internal readonly record struct HeapCheck(string Name, double Raw, double Bound, Term Term, bool Ceiling, bool Strict)
    {
        internal double Adjusted => Ceiling ? Raw + Term.Bytes : Raw - Term.Bytes;
        internal double Margin => Ceiling ? Bound - Adjusted : Adjusted - Bound;
        internal bool Holds => Strict ? Margin > 0 : Margin >= 0;
        internal string Failure => FormattableString.Invariant($"{Name}: raw={Raw}, T={Term.Bytes}, adjusted={Adjusted}, bound={Bound}, margin={Margin}");
    }

    internal static HeapCheck Ceiling(string name, double raw, double bound, Term term, bool strict = false) => new(name, raw, bound, term, true, strict);
    internal static HeapCheck Floor(string name, double raw, double bound, Term term, bool strict = false) => new(name, raw, bound, term, false, strict);

    internal sealed class Batch : IAsyncDisposable
    {
        private readonly PoolEvents _events;
        private readonly ITestOutputHelper _output;
        private readonly string _test;
        private readonly List<Execution> _executions = [];
        private readonly List<CheckGroup> _groups = [];
        private readonly List<Func<ValueTask>> _release = [];

        internal Batch(PoolEvents events, ITestOutputHelper output, string test)
        { _events = events; _output = output; _test = test; }

        internal Sample<T> Measure<T>(FormattableString labels, Func<Func<long>, T> measure)
        {
            var sample = new Sample<T>(this, labels, read => Task.FromResult(measure(read)));
            _executions.Add(sample);
            sample.LoadAsync().GetAwaiter().GetResult();
            return sample;
        }

        internal async Task<Sample<T>> MeasureAsync<T>(FormattableString labels, Func<Func<long>, Task<T>> measure,
            CancellationToken cancellationToken = default)
        {
            var sample = new Sample<T>(this, labels, measure, cancellationToken);
            _executions.Add(sample);
            await sample.LoadAsync();
            return sample;
        }

        internal void Check(Func<HeapCheck[]> checks, Action assert) => CheckAsync(checks, assert).GetAwaiter().GetResult();

        internal Task CheckAsync(Func<HeapCheck[]> checks, Action assert) =>
            CheckAsync(checks, () => { assert(); return Task.CompletedTask; });

        internal async Task CheckAsync(Func<HeapCheck[]> checks, Func<Task> assert)
        {
            _groups.Add(new CheckGroup(checks, assert));
            await ValidateAsync(all: false);
        }

        internal async Task SettleAsync(Func<HeapCheck[]> checks)
        {
            var group = new CheckGroup(checks, () => Task.CompletedTask);
            _groups.Add(group);
            try { await ValidateAsync(all: false); }
            finally { _groups.Remove(group); }
        }

        private async Task ValidateAsync(bool all = true)
        {
            while (true)
            {
                var checks = new List<(CheckGroup Group, HeapCheck[] Checks)>();
                for (int i = all ? 0 : Math.Max(0, _groups.Count - 1); i < _groups.Count; i++) { checks.Add((_groups[i], _groups[i].Checks())); }
                var failed = new List<(CheckGroup Group, HeapCheck Check)>();
                foreach (var (group, groupChecks) in checks)
                {
                    foreach (HeapCheck check in groupChecks) { if (!check.Holds) { failed.Add((group, check)); } }
                }
                // A clean execution retains the original Shouldly expression and fails
                // at the original check. Only a nonzero term can cause remeasurement.
                foreach (var item in failed)
                {
                    if (item.Check.Term.Bytes != 0) { continue; }
                    await item.Group.AssertOnceAsync();
                    false.ShouldBeTrue(item.Check.Failure);
                }
                if (failed.Count != 0)
                {
                    var retry = new List<Execution>();
                    foreach (var item in failed)
                    {
                        foreach (Execution source in item.Check.Term.Sources)
                        {
                            if (source.TrimmedBytes > 0 && !retry.Contains(source)) { retry.Add(source); }
                        }
                    }
                    foreach (Execution execution in retry)
                    {
                        var failures = new List<string>();
                        foreach (var item in failed) { if (Array.IndexOf(item.Check.Term.Sources, execution) >= 0) { failures.Add(item.Check.Failure); } }
                        execution.Reject(failures);
                    }
                    foreach (Execution execution in retry) { await execution.LoadAsync(); }
                    all = true;
                    continue;
                }
                foreach (var (group, groupChecks) in checks)
                {
                    bool clean = true;
                    foreach (HeapCheck check in groupChecks) { clean &= check.Term.Bytes == 0; }
                    if (clean) { await group.AssertOnceAsync(); }
                }
                return;
            }
        }

        // Clean executions can never be selected by a combined check for remeasurement.
        // Dispose their fixture at its original boundary; keep only fixtures whose
        // nonzero terms can still require another execution after this scope ends.
        internal async ValueTask ReleaseOrRetainAsync(IEnumerable<Execution> executions, Func<ValueTask> release)
        {
            if (executions.Any(x => x.TrimmedBytes > 0)) { _release.Add(release); }
            else { await release(); }
        }

        internal IAsyncDisposable LeaseAsync(IAsyncDisposable resource, IEnumerable<Execution> executions) =>
            new FixtureLease(this, executions, resource.DisposeAsync);

        internal IAsyncDisposable Lease(IDisposable resource, IEnumerable<Execution> executions) =>
            new FixtureLease(this, executions, () => { resource.Dispose(); return ValueTask.CompletedTask; });

        private sealed class FixtureLease : IAsyncDisposable
        {
            private readonly Batch _batch;
            private readonly IEnumerable<Execution> _executions;
            private readonly Func<ValueTask> _release;
            internal FixtureLease(Batch batch, IEnumerable<Execution> executions, Func<ValueTask> release)
            { _batch = batch; _executions = executions; _release = release; }
            public ValueTask DisposeAsync() => _batch.ReleaseOrRetainAsync(_executions, _release);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var release in _release) { await release(); }
            _release.Clear();
        }

        internal void Complete() => CompleteAsync().GetAwaiter().GetResult();

        internal async Task CompleteAsync()
        {
            await ValidateAsync();
            foreach (CheckGroup group in _groups) { await group.AssertOnceAsync(); }
            var checks = new List<HeapCheck>();
            foreach (CheckGroup group in _groups) { checks.AddRange(group.Checks()); }
            foreach (Execution execution in _executions)
            {
                if (execution.TrimmedBytes <= 0) { continue; }
                double margin = double.PositiveInfinity;
                foreach (HeapCheck check in checks) { if (Array.IndexOf(check.Term.Sources, execution) >= 0) { margin = Math.Min(margin, check.Margin); } }
                _output.WriteLine(string.Concat("POOL_TRIM_ACCEPT|", execution.Name,
                    "|attempt=", execution.Attempt.ToString(CultureInfo.InvariantCulture),
                    "|trimmedBytes=", execution.TrimmedBytes.ToString(CultureInfo.InvariantCulture),
                    "|margin=", margin.ToString("R", CultureInfo.InvariantCulture)));
            }
        }

        private sealed class CheckGroup
        {
            private readonly Func<Task> _assert;
            private bool _asserted;
            internal Func<HeapCheck[]> Checks { get; }
            internal CheckGroup(Func<HeapCheck[]> checks, Func<Task> assert) { Checks = checks; _assert = assert; }
            internal async Task AssertOnceAsync() { if (!_asserted) { _asserted = true; await _assert(); } }
        }

        internal PoolEvents Events => _events;
        internal ITestOutputHelper Output => _output;
        internal string Test => _test;
    }

    internal abstract class Execution
    {
        private readonly Batch _batch;
        private readonly FormattableString _labels;
        private readonly List<AttemptRecord> _history = [];
        private readonly CancellationToken _cancellationToken;
        internal int Attempt => _history.Count;
        internal long TrimmedBytes => _history.Count == 0 ? 0 : _history[^1].Bytes;
        internal Term Term => new(TrimmedBytes, [this]);
        internal string Name => string.Concat("test=", _batch.Test, "|", _labels.ToString(CultureInfo.CurrentCulture));

        private protected Execution(Batch batch, FormattableString labels, CancellationToken cancellationToken)
        { _batch = batch; _labels = labels; _cancellationToken = cancellationToken; }

        internal async Task LoadAsync()
        {
            Clear();
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var window = new Window(_batch.Events);
                if (await LoadAttemptAsync(window))
                {
                    if (window.TrimmedBytes == 0) { SealClean(); }
                    return;
                }
                Reject(["unsized trims"]);
            }
        }

        internal void Record(Window window) => _history.Add(new(window.Trims, window.TrimmedBytes, window.UnsizedTrims));
        private protected abstract void Clear();
        private protected abstract void SealClean();
        private protected abstract Task<bool> LoadAttemptAsync(Window window);

        internal void Reject(IEnumerable<string> failures)
        {
            AttemptRecord current = _history[^1];
            current.Failures.AddRange(failures);
            _batch.Output.WriteLine(string.Concat("POOL_TRIM_REMEASURE|", Name,
                "|attempt=", Attempt.ToString(CultureInfo.InvariantCulture), "|trims=", current.Trims.ToString(CultureInfo.InvariantCulture),
                "|trimmedBytes=", current.Bytes.ToString(CultureInfo.InvariantCulture), "|unsizedTrims=", current.Unsized.ToString(CultureInfo.InvariantCulture)));
            if (Attempt == 4)
            {
                string attempts = string.Join("; ", _history.Select((x, i) => FormattableString.Invariant(
                    $"attempt {i + 1}: trims={x.Trims}, trimmedBytes={x.Bytes}, unsizedTrims={x.Unsized}, checks=[{string.Join("; ", x.Failures)}]")));
                false.ShouldBeTrue($"Pool trim terms prevented acceptance of all 4 attempts of {Name}; {attempts}");
            }
        }

        private sealed class AttemptRecord
        {
            internal long Trims { get; }
            internal long Bytes { get; }
            internal long Unsized { get; }
            internal List<string> Failures { get; } = [];
            internal AttemptRecord(long trims, long bytes, long unsized) { Trims = trims; Bytes = bytes; Unsized = unsized; }
        }
    }

    internal sealed class Sample<T> : Execution
    {
        private Func<Func<long>, Task<T>>? _measure;
        internal T Value { get; private set; } = default!;
        internal Sample(Batch batch, FormattableString labels, Func<Func<long>, Task<T>> measure, CancellationToken cancellationToken = default)
            : base(batch, labels, cancellationToken) { _measure = measure; }

        private protected override void Clear() => Value = default!;
        private protected override void SealClean() => _measure = null;

        // Returning from this frame drops a rejected attempt's result graph before
        // the next drain/baseline, including unsized-trim remeasurements.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private protected override async Task<bool> LoadAttemptAsync(Window window)
        {
            T result = await _measure!(window.Read);
            Record(window);
            if (window.UnsizedTrims != 0) { return false; }
            Value = result;
            return true;
        }
    }

    internal sealed class Window
    {
        private readonly PoolEvents _events;
        private bool _started;
        private long _trimsBefore, _bytesBefore, _unsizedBefore;
        private long _trimsAfter, _bytesAfter, _unsizedAfter;
        internal long Trims => _trimsAfter - _trimsBefore;
        internal long TrimmedBytes => _bytesAfter - _bytesBefore;
        internal long UnsizedTrims => _unsizedAfter - _unsizedBefore;
        internal Window(PoolEvents events) { _events = events; }

        internal long Read()
        {
            if (!_started)
            {
                _started = true;
                // A trim during this first collection frees its arrays before the baseline's own
                // collections, so it lowers the baseline and every later reading alike and cannot
                // move a measured delta. Run that pass first and open the window after it; a later
                // trim lowers only the readings after it, so it still counts.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                _bytesBefore = Volatile.Read(ref _events.Trimmed);
                _unsizedBefore = Volatile.Read(ref _events.UnsizedTrims);
                _trimsBefore = Volatile.Read(ref _events.TrimEvents);
            }
            long live = LiveHeap.Read();
            Maximum(ref _trimsAfter, Volatile.Read(ref _events.TrimEvents));
            Maximum(ref _bytesAfter, Volatile.Read(ref _events.Trimmed));
            Maximum(ref _unsizedAfter, Volatile.Read(ref _events.UnsizedTrims));
            return live;
        }

        private static void Maximum(ref long target, long value)
        {
            long previous;
            do { previous = Volatile.Read(ref target); }
            while (previous < value && Interlocked.CompareExchange(ref target, value, previous) != previous);
        }
    }
}
