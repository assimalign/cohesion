using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

/// <summary>
/// The B+Tree index's diagnostics: index DDL, the page-format and corruption failures that refuse
/// an index, the split invariants that fail an insert, the splits themselves, and the open-time
/// purge of unproven writers.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Indexing</c>; applications
/// forward it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every engine
/// model's secondary indexes are <see cref="BTreeIndex"/> trees under a <see cref="BTreeIndexManager"/>,
/// so this one source covers the indexes of every engine. The <c>database</c> payload is the name
/// of the storage whose pages hold the tree.
/// </para>
/// <para>
/// No payload carries a key, an entry reference or any other indexed value: an index is named by
/// its owning object's id and its name, and a page by its id. No counters: an index has no
/// transition that is not already counted by the storage under it.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Indexing")]
internal sealed class IndexEventSource : EventSource
{
    public static readonly IndexEventSource Log = new();

    private IndexEventSource()
    {
    }

    /// <summary>
    /// The keyword of the per-split <see cref="EventLevel.Verbose"/> events, so a tool can take them
    /// without the rest.
    /// </summary>
    public static class Keywords
    {
        /// <summary>Page splits and root growth.</summary>
        public const EventKeywords Splits = (EventKeywords)0x1;
    }

    /// <summary>Writes that an index was created on an object.</summary>
    [NonEvent]
    public void IndexCreated(Storage.Storage storage, ulong objectId, IndexDefinition definition)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            IndexCreated(storage.Name, (long)objectId, definition.Name, definition.Kind.ToString());
        }
    }

    /// <summary>Writes that an index was dropped from an object.</summary>
    [NonEvent]
    public void IndexDropped(Storage.Storage storage, ulong objectId, string index)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            IndexDropped(storage.Name, (long)objectId, index);
        }
    }

    /// <summary>Writes that an existing tree's root page is not in the engine's page format, so the tree is refused.</summary>
    [NonEvent]
    public void IndexFormatRefused(Storage.Storage storage, BTreeIndexRegistration registration, int foundFormat)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            IndexFormatRefused(storage.Name, (long)registration.ObjectId, registration.Definition.Name, registration.RootPageId, foundFormat);
        }
    }

    /// <summary>Writes that an operation reached a page inside an attached tree that is not a node of the engine's format.</summary>
    [NonEvent]
    public void IndexCorruptionDetected(Storage.Storage storage, string index, long pageId, int formatVersion)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            IndexCorruptionDetected(storage.Name, index, pageId, formatVersion);
        }
    }

    /// <summary>Writes that a split found the tree out of the order it must keep, and failed the insert.</summary>
    /// <param name="storage">The storage holding the tree.</param>
    /// <param name="index">The index's name.</param>
    /// <param name="pageId">The page the split was working on.</param>
    /// <param name="detail">The message of the <see cref="IndexException"/> the insert fails with; it names pages and positions, never keys.</param>
    [NonEvent]
    public void IndexInvariantViolated(Storage.Storage storage, string index, long pageId, string detail)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            IndexInvariantViolated(storage.Name, index, pageId, detail);
        }
    }

    /// <summary>Writes that a full node page split in two.</summary>
    /// <param name="storage">The storage holding the tree.</param>
    /// <param name="index">The index's name.</param>
    /// <param name="pageId">The page that split; its upper half moved to a new sibling.</param>
    /// <param name="leaf">True for a leaf, false for an internal node.</param>
    /// <param name="entries">The entries the page held before the split.</param>
    [NonEvent]
    public void PageSplit(Storage.Storage storage, string index, long pageId, bool leaf, int entries)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Splits))
        {
            PageSplit(storage.Name, index, pageId, leaf, entries);
        }
    }

    /// <summary>Writes that the root split: the tree grew one level, and its root page stayed in place.</summary>
    [NonEvent]
    public void RootGrown(Storage.Storage storage, string index, long rootPageId)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Splits))
        {
            RootGrown(storage.Name, index, rootPageId);
        }
    }

    /// <summary>Writes one purge of unproven writers' stamps from every live index of a storage.</summary>
    /// <param name="storage">The storage holding the trees.</param>
    /// <param name="writers">The writers purged.</param>
    /// <param name="entriesRemoved">The entries removed or restored.</param>
    /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> taken when the purge began.</param>
    [NonEvent]
    public void WritersPurged(Storage.Storage storage, int writers, long entriesRemoved, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            WritersPurged(storage.Name, writers, entriesRemoved, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Database '{0}' created {3} index '{2}' on object {1}.")]
    private void IndexCreated(string database, long objectId, string index, string kind)
        => WriteEvent(1, database, objectId, index, kind);

    [Event(2, Level = EventLevel.Verbose, Message = "Database '{0}' dropped index '{2}' from object {1}.")]
    private void IndexDropped(string database, long objectId, string index)
        => WriteEvent(2, database, objectId, index);

    [Event(3, Level = EventLevel.Error, Message = "Database '{0}' refused index '{2}' on object {1}: its root page {3} is in page format {4}, not the engine's.")]
    private void IndexFormatRefused(string database, long objectId, string index, long rootPageId, int foundFormat)
        => WriteEvent(3, database, objectId, index, rootPageId, foundFormat);

    [Event(4, Level = EventLevel.Error, Message = "Database '{0}' found index '{1}' corrupt: page {2} is not a node of the engine's page format (found format {3}).")]
    private void IndexCorruptionDetected(string database, string index, long pageId, int formatVersion)
        => WriteEvent(4, database, index, pageId, formatVersion);

    [Event(5, Level = EventLevel.Error, Message = "Database '{0}' index '{1}' failed a split invariant on page {2}: {3}")]
    private void IndexInvariantViolated(string database, string index, long pageId, string detail)
        => WriteEvent(5, database, index, pageId, detail);

    [Event(6, Level = EventLevel.Verbose, Keywords = Keywords.Splits, Message = "Database '{0}' index '{1}' split page {2} (leaf: {3}) holding {4} entries.")]
    private void PageSplit(string database, string index, long pageId, bool leaf, int entries)
        => WriteEvent(6, database, index, pageId, leaf, entries);

    [Event(7, Level = EventLevel.Verbose, Keywords = Keywords.Splits, Message = "Database '{0}' index '{1}' grew a level: root page {2} split in place.")]
    private void RootGrown(string database, string index, long rootPageId)
        => WriteEvent(7, database, index, rootPageId);

    [Event(8, Level = EventLevel.Informational, Message = "Database '{0}' purged {1} unproven writers from its indexes: {2} entries removed or restored in {3} ms.")]
    private void WritersPurged(string database, int writers, long entriesRemoved, double durationMilliseconds)
        => WriteEvent(8, database, writers, entriesRemoved, durationMilliseconds);
}
