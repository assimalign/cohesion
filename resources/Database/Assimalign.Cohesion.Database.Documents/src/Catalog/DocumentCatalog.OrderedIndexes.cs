using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

public sealed partial class DocumentCatalog
{
    // Instance-local progress seam, after the cursor released every page/latch and
    // before reading metadata. Tests can commit concurrent mutations at this point.
    internal Action? OrderedIndexEntryRead { get; set; }

    // The caller owns the statement snapshot pin. Keys are cursor-owned arrays,
    // safe to retain after the next move; no monitor or latch survives a yield.
    internal async IAsyncEnumerable<(IndexKey Key, DocumentCatalogEntry Document)> ReadIndexInOrderAsync(
        DocumentIndexMetadata definition, IReadOnlyList<object> equalityPrefix,
        object? lower, bool includeLower, object? upper, bool includeUpper, bool reverse,
        TransactionSnapshot snapshot, Action entryRead,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = ResolveIndex(definition);
        var range = SeekRange(definition, equalityPrefix, lower, includeLower, upper, includeUpper);
        await using var cursor = index.OpenCursor(snapshot, range, reverse, cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            entryRead();
            OrderedIndexEntryRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var (page, slot) = DocumentStorage.UnpackLocation(cursor.CurrentEntryReference);
            if (!_storage.TryReadRecord(page, slot, 0, out var bytes)) { continue; }
            var record = DocumentCatalogCodec.Decode(bytes.Span, page, slot);
            var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
            if (record.Document is { } document && document.CollectionId == definition.CollectionId &&
                snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter)))
            {
                yield return (cursor.CurrentKey, document);
            }
        }
    }
}
