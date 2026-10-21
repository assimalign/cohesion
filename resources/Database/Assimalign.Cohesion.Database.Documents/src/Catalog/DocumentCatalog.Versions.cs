using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

public sealed partial class DocumentCatalog
{
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
        public TransactionSequence RecoveredDeleter { get; set; }
        public bool PredecessorKnown { get; set; }
        public Reference? Previous { get; set; }
        public Reference? Next { get; set; }
    }

    private sealed class Versions
    {
        private readonly Dictionary<(PageId Page, int Slot), Reference> _locations = new();
        private Reference? _sweep;
        private bool _discarded;

        public Reference? First { get; private set; }
        public Reference? Current { get; private set; }
        public bool TerminalKnown { get; private set; } = true;
        public int Count => _discarded ? 0 : _locations.Count;
        public LinkedListNode<Versions>? MaintenanceNode { get; set; }
        public Func<CatalogRecord, bool> Matches { get; set; } = null!;
        public Action Retire { get; set; } = null!;

        public void Add(Reference reference, TransactionSequence? recoveredDeleter)
        {
            if (_locations.TryGetValue((reference.PageId, reference.SlotIndex), out var reused))
            {
                Remove(reused);
            }
            // Earlier own saves have writer == deleter and cannot be visible to any
            // snapshot. Keep one publication per writer, including its committed predecessor.
            if (recoveredDeleter is null && Current is { } own && own.Writer == reference.Writer)
            {
                Remove(own);
            }
            reference.RecoveredDeleter = recoveredDeleter ?? TransactionSequence.None;
            reference.PredecessorKnown = TerminalKnown;
            reference.Previous = Current;
            if (Current is { } previous) { previous.Next = reference; }
            else { First = reference; }
            Current = reference;
            TerminalKnown = true;
            _locations.Add((reference.PageId, reference.SlotIndex), reference);
        }

        public void RebuildPublicationOrder()
        {
            // Physical order and transaction begin order are not publication order.
            // A predecessor's deleter identifies the writer that replaced it, including
            // a ReadCommitted writer that began earlier. Self-tombstones are never visible.
            var references = _locations.Values.ToArray();
            foreach (var reference in references)
            {
                if (reference.RecoveredDeleter != TransactionSequence.None && reference.Writer == reference.RecoveredDeleter) { Remove(reference); }
            }
            references = _locations.Values.OrderBy(reference => reference.Writer.Value).ToArray();
            var writers = new Dictionary<TransactionSequence, Reference>();
            bool unique = true;
            foreach (var reference in references) { unique &= writers.TryAdd(reference.Writer, reference); }
            var incoming = references.ToDictionary(reference => reference, _ => 0);
            int edges = 0;
            foreach (var reference in references)
            {
                if (writers.TryGetValue(reference.RecoveredDeleter, out var successor))
                {
                    incoming[successor]++;
                    edges++;
                }
            }
            var ready = new PriorityQueue<Reference, ulong>();
            foreach (var reference in references)
            {
                if (incoming[reference] == 0) { ready.Enqueue(reference, reference.Writer.Value); }
            }
            var ordered = new List<Reference>(references.Length);
            while (ready.TryDequeue(out var reference, out _))
            {
                ordered.Add(reference);
                if (writers.TryGetValue(reference.RecoveredDeleter, out var successor)
                    && --incoming[successor] == 0)
                {
                    ready.Enqueue(successor, successor.Writer.Value);
                }
            }
            bool publicationOrdered = unique && references.GroupBy(reference => reference.RecoveredDeleter).All(group => group.Key == TransactionSequence.None || group.Count() == 1) && ordered.Count == references.Length
                && edges == Math.Max(0, references.Length - 1);
            // Disconnected delete/recreate histories or ambiguous stamps use full
            // highest-visible-writer selection, never a terminal-head shortcut.
            if (ordered.Count != references.Length) { ordered = references.ToList(); }
            var live = references.Where(reference => reference.RecoveredDeleter == TransactionSequence.None).ToArray();
            TerminalKnown = publicationOrdered || live.Length == 1;
            if (live.Length == 1)
            {
                // A disconnected earlier deletion/recreation must not place an older
                // component after the unique live terminal (its writer may have begun first).
                var component = new HashSet<Reference>();
                var terminal = live[0];
                component.Add(terminal);
                for (int index = ordered.Count - 1; index >= 0; index--)
                {
                    var candidate = ordered[index];
                    if (writers.TryGetValue(candidate.RecoveredDeleter, out var successor) && component.Contains(successor))
                    {
                        component.Add(candidate);
                    }
                }
                ordered = ordered.Where(reference => !component.Contains(reference))
                    .Concat(ordered.Where(component.Contains)).ToList();
            }
            First = null;
            Current = null;
            foreach (var reference in ordered)
            {
                reference.PredecessorKnown = Current is null || Current.RecoveredDeleter == reference.Writer;
                reference.Previous = Current;
                reference.Next = null;
                if (Current is { } previous) { previous.Next = reference; }
                else { First = reference; }
                Current = reference;
            }
            _sweep = First;
        }

        public void Remove(Reference reference)
        {
            _locations.Remove((reference.PageId, reference.SlotIndex));
            if (ReferenceEquals(Current, reference))
            {
                TerminalKnown &= reference.PredecessorKnown;
                Current = reference.Previous;
            }
            if (reference.Previous is { } previous) { previous.Next = reference.Next; }
            else { First = reference.Next; }
            if (reference.Next is { } next)
            {
                next.Previous = reference.Previous;
                next.PredecessorKnown &= reference.PredecessorKnown;
            }
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

        public void Discard()
        {
            // Retiring the directory value releases the whole map without an O(n) clear.
            _discarded = true;
            First = null;
            Current = null;
            _sweep = null;
        }
    }
}
