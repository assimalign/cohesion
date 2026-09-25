using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.OpenTelemetry;

/// <summary>Exports logs without propagating collector failures to application code.</summary>
public interface IOtlpLogExporter : IAsyncDisposable
{
    /// <summary>Attempts a nonblocking enqueue. Overflow drops the oldest record.</summary>
    /// <param name="record">The record to enqueue.</param>
    /// <returns>Whether the record was queued.</returns>
    bool TryEnqueue(OtlpLogRecord record);
    /// <summary>Exports a batch within the configured total timeout, including bounded retries.</summary>
    /// <param name="batch">Records to export.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the collector accepted the entire batch.</returns>
    ValueTask<bool> ExportAsync(ReadOnlyMemory<OtlpLogRecord> batch, CancellationToken cancellationToken = default);
    /// <summary>Drains the queue; collector failures and cancellation are counted and isolated.</summary>
    /// <param name="cancellationToken">Limits the drain budget.</param>
    /// <returns>The asynchronous drain.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets the number of records dropped because of queue overflow or shutdown.</summary>
    long DroppedCount { get; }
    /// <summary>Gets the number of unsuccessful export attempts.</summary>
    long FailedExportCount { get; }
}
