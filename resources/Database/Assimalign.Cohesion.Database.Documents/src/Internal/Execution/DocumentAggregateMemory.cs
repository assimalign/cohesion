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

    internal DocumentAggregateMemory(long limit, CancellationToken cancellationToken)
    {
        _limit = limit;
        _cancellationToken = cancellationToken;
    }

    internal long Retain(object? value, bool array = false)
    {
        // Sorted-set nodes/list capacity, scalar ownership and JSON text/token metadata.
        // ARRAY_AGG also reserves its serialized result while the input list is alive.
        long bytes = 64L + (value switch
        {
            string text => 32L + text.Length * (array ? 8L : 2L),
            JsonElement json => (array ? 2L : 1L) *
                (128L + JsonMarshal.GetRawUtf8Value(json).Length + 12L * Tokens(json)),
            _ => 32L,
        });
        Charge(bytes);
        return bytes;
    }

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
        return true;
    }

    internal void Release(long bytes) => _bytes -= bytes;

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
