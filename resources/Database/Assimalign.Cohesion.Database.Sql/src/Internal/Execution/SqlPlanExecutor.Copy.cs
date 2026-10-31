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
        byte[] bytes = new byte[16 * 1024];
        async ValueTask WriteAsync()
        {
            try
            {
                // A stored row is bounded by the engine's record limit; casts can produce longer
                // text, so encode it into fixed chunks, including split surrogate pairs.
                var encoder = utf8.GetEncoder();
                foreach (var chunk in row.GetChunks())
                {
                    int offset = 0;
                    do
                    {
                        encoder.Convert(chunk.Span[offset..], bytes, false, out int used, out int written, out _);
                        offset += used;
                        if (written > 0) { await DeliverAsync(written).ConfigureAwait(false); }
                    } while (offset < chunk.Length);
                }
                encoder.Convert(ReadOnlySpan<char>.Empty, bytes, true, out _, out int tail, out _);
                if (tail > 0) { await DeliverAsync(tail).ConfigureAwait(false); }
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
        // The unscoped iterator captures a scalar page ceiling instead of an O(table-pages)
        // owner directory. COPY alone pays an O(database-pages) scan to keep retained memory
        // independent of table size; SELECT's existing owner-scoped access path is unchanged.
        using var iterator = _storage.GetUnitIterator();
        PageId? page = null;
        bool targetPage = false;
        while (iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unit = iterator.Current;
            if (unit.Data.Length < SqlRowCodec.StampHeaderSize)
            { throw new StorageCorruptionException(unit.PageId, unit.SlotIndex, ErrorMessages.CopyRowMalformed); }
            var (writer, deleter) = SqlRowCodec.ReadStamps(unit.Data.Span);
            if (!statement.Snapshot.IsVisible(writer) || deleter != TransactionSequence.None && statement.Snapshot.IsVisible(deleter))
            { continue; }
            // Discard invisible private copies before consulting page ownership. Rollback may
            // free/reassign an uncommitted page after the iterator copied a row from it. A visible
            // row's page instead stays retained by this snapshot and the reclamation epoch.
            if (page != unit.PageId)
            {
                using var header = _storage.PageManager.GetPage(unit.PageId);
                targetPage = header.Page.OwnerId == plan.Table.ObjectId;
                page = unit.PageId;
            }
            if (!targetPage) { continue; }
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
        return new SqlQueryResult(DatabaseResultStatus.Success, count);
    }
}
