using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // COPY-only observations for deterministic scan-cost and reclamation regressions.
    internal static Action<StorageUnitIterator>? CopyIteratorOpened { get; set; }
    internal static Action<StorageUnit>? CopyUnitRead { get; set; }

    internal async Task<DatabaseResult> CopyToAsync(SqlSelectPlan plan, SqlCopyOptions options, Stream destination,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = ExecutionEvaluator(null, cancellationToken);
        var forced = plan.Projections.Select(projection => options.ForceQuoteAll ||
            options.ForceQuote.Contains(projection.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var row = new StringBuilder();
        // Do not dispose a StreamWriter with pending data after cancellation or failure: its
        // Dispose would write again without the operation token. The encoder owns no resource.
        var utf8 = new UTF8Encoding(false, true);
        var encoder = utf8.GetEncoder();
        byte[] bytes = new byte[16 * 1024];
        int buffered = 0;
        async ValueTask WriteAsync()
        {
            try
            {
                // A stored row is bounded by the engine's record limit; casts can produce longer
                // text, so encode it into fixed chunks, including split surrogate pairs.
                foreach (var chunk in row.GetChunks())
                {
                    int offset = 0;
                    while (offset < chunk.Length)
                    {
                        // Leave room for the next complete UTF-8 scalar, including a low
                        // surrogate whose high surrogate the encoder retained from a chunk.
                        int scalarBytes = chunk.Span[offset] switch
                        {
                            <= '\u007f' => 1,
                            <= '\u07ff' => 2,
                            >= '\ud800' and <= '\udfff' => 4,
                            _ => 3,
                        };
                        if (bytes.Length - buffered < scalarBytes)
                        { await DeliverAsync(buffered).ConfigureAwait(false); buffered = 0; }
                        encoder.Convert(chunk.Span[offset..], bytes.AsSpan(buffered), false, out int used, out int written, out _);
                        offset += used;
                        buffered += written;
                    }
                }
                encoder.Convert(ReadOnlySpan<char>.Empty, bytes.AsSpan(buffered), true, out _, out int tail, out _);
                buffered += tail;
                if (buffered == bytes.Length) { await DeliverAsync(buffered).ConfigureAwait(false); buffered = 0; }
                row.Clear();
            }
            catch (EncoderFallbackException exception) { throw SqlResultPreparation.TranslateFailure(exception); }
        }
        async ValueTask DeliverAsync(int length)
        {
            try { await destination.WriteAsync(bytes.AsMemory(0, length), cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            { throw new SqlCopyDeliveryException(exception); }
        }
        if (options.Header)
        {
            for (int i = 0; i < plan.Projections.Count; i++)
            {
                if (i > 0) { row.Append(options.Delimiter); }
                SqlCopyEncoder.Append(row, plan.Projections[i].Name, options, false, plan.Projections.Count == 1);
            }
            row.Append('\n');
            await WriteAsync().ConfigureAwait(false);
        }
        long count = 0;
        var defaults = _definitions.Get(plan.Table, cancellationToken).MissingValues;
        // Lock/latch order: root session admission -> snapshot -> catalog publication monitor
        // (binding only) -> page pin / optimistic page read. No row/object/writer lock or page
        // latch spans a destination await. The iterator retains at most one nonlocking pin.
        // Share SELECT's owner-scoped access path. Membership is checked while the iterator
        // copies the row, so neither unrelated rows/indexes nor a later page-header lookup
        // can race an already retired owner's reclamation.
        using var iterator = _storage.GetUnitIterator(plan.Table.ObjectId);
        CopyIteratorOpened?.Invoke(iterator);
        while (iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unit = iterator.Current;
            CopyUnitRead?.Invoke(unit);
            if (unit.Data.Length < SqlRowCodec.StampHeaderSize)
            { throw new StorageCorruptionException(unit.PageId, unit.SlotIndex, ErrorMessages.CopyRowMalformed); }
            var (writer, deleter) = SqlRowCodec.ReadStamps(unit.Data.Span);
            if (!statement.Snapshot.IsVisible(writer) || deleter != TransactionSequence.None && statement.Snapshot.IsVisible(deleter))
            { continue; }
            statement.Metrics.RecordsExamined++;
            var values = DecodeRow(unit.Data, unit.PageId, unit.SlotIndex, plan.Table, defaults, out _, out _);
            for (int i = 0; i < plan.Projections.Count; i++)
            {
                if (i > 0) { row.Append(options.Delimiter); }
                var text = (string?)evaluator.Evaluate(plan.Projections[i].Value!, values);
                SqlCopyEncoder.Append(row, text, options, forced[i], plan.Projections.Count == 1);
            }
            row.Append('\n');
            await WriteAsync().ConfigureAwait(false);
            count++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (buffered > 0) { await DeliverAsync(buffered).ConfigureAwait(false); }
        return new SqlQueryResult(DatabaseResultStatus.Success, count);
    }
}
