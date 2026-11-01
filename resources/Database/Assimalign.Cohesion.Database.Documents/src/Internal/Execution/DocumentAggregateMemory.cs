using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentAggregateMemory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentAggregateMemory.DebugView))]
internal sealed class DocumentAggregateMemory
{
    private readonly long _limit;
    private readonly CancellationToken _cancellationToken;
    private long _bytes;
    private int _generation;

    internal long Bytes => _bytes;
    internal int Generation => _generation;
    internal long PeakBytes { get; private set; }

    // Internal retained-heap probe; absent in ordinary execution.
    internal Action? RetentionObserved { get; set; }

    internal DocumentAggregateMemory(long limit, CancellationToken cancellationToken)
    {
        _limit = limit;
        _cancellationToken = cancellationToken;
    }

    internal long Retain(object? value)
    {
        long bytes = 64L + ValueBytes(value);
        Charge(bytes);
        return bytes;
    }

    // One owned representation, independent of whether it is input, scratch or output.
    internal long ValueBytes(object? value) => value switch
    {
        string text => 32L + text.Length * 2L,
        JsonElement json => 128L + JsonMarshal.GetRawUtf8Value(json).Length + 12L * Tokens(json),
        _ => 32L,
    };

    internal void Charge(long bytes)
    {
        if (!TryCharge(bytes))
        { throw new DocumentPlanningException("CD05018", string.Format(CultureInfo.CurrentCulture, ErrorMessages.AggregateMemoryExceeded, _limit)); }
    }

    internal bool TryCharge(long bytes)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (bytes > _limit - _bytes) { return false; }
        _bytes += bytes;
        PeakBytes = Math.Max(PeakBytes, _bytes);
        RetentionObserved?.Invoke();
        return true;
    }

    internal void Release(long bytes) => _bytes -= bytes;

    internal void Clear() { _bytes = 0; _generation++; }

    internal void ReleaseResult(long bytes, int generation)
    { if (generation == _generation) { Release(bytes); } }

    // Lifetime owner for source rows, group keys/state, GROUP AS wrappers and DML buffers.
    // Each owner releases only its own reservations, including on refused/canceled work.
    internal Reservation Reserve() => new(this);

    [global::System.Diagnostics.DebuggerDisplay("Reservation")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(Reservation.DebugView))]
    internal sealed class Reservation : IDisposable
    {
        private readonly DocumentAggregateMemory _memory;
        private long _bytes;

        internal long Bytes => _bytes;

        internal Reservation(DocumentAggregateMemory memory) { _memory = memory; }

        internal void Charge(long bytes) { _memory.Charge(bytes); _bytes += bytes; }

        internal void Retain(object? value) => Charge(64L + _memory.ValueBytes(value));

        // Transfer a successful materialization's charge without releasing it from
        // the shared counter. The caller adopts immediately, before any further work.
        internal long Detach() { long bytes = _bytes; _bytes = 0; return bytes; }

        internal void Adopt(long bytes) => _bytes += bytes;

        internal void Rewind(long bytes)
        {
            _memory.Release(_bytes - bytes);
            _bytes = bytes;
        }

        public void Dispose()
        {
            _memory.Release(_bytes);
            _bytes = 0;
        }

        private sealed class DebugView
        {
            public DebugView(Reservation value) { _ = value; }
        }
    }

    private long Tokens(JsonElement json)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        long count = 1;
        if (json.ValueKind == JsonValueKind.Array)
        {
            count++;
            foreach (var item in json.EnumerateArray()) { count += Tokens(item); }
        }
        else if (json.ValueKind == JsonValueKind.Object)
        {
            count++;
            foreach (var property in json.EnumerateObject()) { count += 1 + Tokens(property.Value); }
        }
        return count;
    }

    private sealed class DebugView
    {
        public DebugView(DocumentAggregateMemory value) { _ = value; }
    }
}
