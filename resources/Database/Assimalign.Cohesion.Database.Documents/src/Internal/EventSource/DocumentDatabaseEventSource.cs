using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>
/// The document model's diagnostics: the index recovery a reopened database runs.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Documents</c>; applications
/// forward it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. The model has
/// no wire server, and its statements, sessions, transactions and storage are reported by the root,
/// Transactions and Storage sources, so this source writes only what the model owns: the recovery
/// of its secondary indexes from the writers the journal's analysis found aborted. The Graph model
/// writes the same two events from its own source (ids 10 and 11 there). No counters.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Documents")]
internal sealed class DocumentDatabaseEventSource : EventSource
{
    public static readonly DocumentDatabaseEventSource Log = new();

    private DocumentDatabaseEventSource()
    {
    }

    /// <summary>
    /// Writes that a reopened database starts recovering its indexes from the writers the
    /// journal's analysis found aborted.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="abortedWriters">The aborted writers whose index entries the recovery removes.</param>
    /// <returns>The timestamp <see cref="IndexRecoveryStop"/> measures from, or zero while the event is disabled.</returns>
    [NonEvent]
    public long IndexRecoveryStart(DatabaseName database, int abortedWriters)
    {
        if (!IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            return 0;
        }

        IndexRecoveryStart(database.ToString(), abortedWriters);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Writes that a reopened database recovered its indexes; written only for a recovery whose
    /// start was written.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="startTimestamp">The timestamp <see cref="IndexRecoveryStart(DatabaseName, int)"/> returned.</param>
    [NonEvent]
    public void IndexRecoveryStop(DatabaseName database, long startTimestamp)
    {
        if (startTimestamp != 0 && IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            IndexRecoveryStop(database.ToString(), Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Database '{0}' started recovering its indexes from {1} aborted writer(s).")]
    private void IndexRecoveryStart(string database, int abortedWriters)
        => WriteEvent(1, database, abortedWriters);

    [Event(2, Level = EventLevel.Informational, Message = "Database '{0}' recovered its indexes in {1} ms.")]
    private void IndexRecoveryStop(string database, double durationMilliseconds)
        => WriteEvent(2, database, durationMilliseconds);
}
