using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

public sealed partial class DocumentCatalog
{
    // A writer can begin before another writer but publish later. Keep writer order,
    // breaking ties by publication order for repeated saves in one transaction.
    private sealed class Reference
    {
        public Reference(PageId pageId, int slotIndex, TransactionSequence writer)
        {
            PageId = pageId;
            SlotIndex = slotIndex;
            Writer = writer;
        }

        public PageId PageId { get; }
        public int SlotIndex { get; }
        public TransactionSequence Writer { get; }
        public long Order { get; set; }
        public Reference? Previous { get; set; }
        public Reference? Next { get; set; }
    }

    private sealed class Versions
    {
        private readonly SortedSet<Reference> _ordered = new(Comparer<Reference>.Create((left, right) =>
        {
            int writer = left.Writer.Value.CompareTo(right.Writer.Value);
            return writer != 0 ? writer : left.Order.CompareTo(right.Order);
        }));
        private readonly Dictionary<(PageId Page, int Slot), Reference> _locations = new();
        private long _order;
        private Reference? _sweep;
        private bool _recoveredCurrentDeleted;

        public Reference? First { get; private set; }
        public Reference? Last { get; private set; }
        public int Count => _locations.Count;
        public Reference? Current { get; set; }

        public void Add(Reference reference, TransactionSequence? recoveredDeleter)
        {
            if (_locations.TryGetValue((reference.PageId, reference.SlotIndex), out var reused))
            {
                Remove(reused);
            }

            reference.Order = ++_order;
            Reference? next = null;
            if (Last is { } last && reference.Writer < last.Writer)
            {
                next = _ordered.GetViewBetween(reference, last).Min;
            }
            var previous = next is null ? Last : next.Previous;
            reference.Previous = previous;
            reference.Next = next;
            if (previous is null) { First = reference; }
            else { previous.Next = reference; }
            if (next is null) { Last = reference; }
            else { next.Previous = reference; }
            _ordered.Add(reference);
            _locations.Add((reference.PageId, reference.SlotIndex), reference);
            // Recovery has removed unfinished writers. Any untombstoned version wins
            // over a deleted one, independently of physical scan order; otherwise keep
            // a deletion marker. Normal saves publish the newest logical head directly.
            if (recoveredDeleter is null || Current is null
                || (recoveredDeleter == TransactionSequence.None
                    && (_recoveredCurrentDeleted || reference.Writer >= Current.Writer)))
            {
                Current = reference;
                _recoveredCurrentDeleted = recoveredDeleter is not null && recoveredDeleter != TransactionSequence.None;
            }
        }

        public void Remove(Reference reference)
        {
            _ordered.Remove(reference);
            _locations.Remove((reference.PageId, reference.SlotIndex));
            if (ReferenceEquals(Current, reference)) { Current = null; }
            if (reference.Previous is { } previous) { previous.Next = reference.Next; }
            else { First = reference.Next; }
            if (reference.Next is { } next) { next.Previous = reference.Previous; }
            else { Last = reference.Previous; }
            if (ReferenceEquals(_sweep, reference)) { _sweep = reference.Next ?? First; }
            reference.Previous = null;
            reference.Next = null;
        }

        public Reference? NextSweep()
        {
            var reference = _sweep ?? First;
            _sweep = reference?.Next ?? First;
            return reference;
        }
    }
}
