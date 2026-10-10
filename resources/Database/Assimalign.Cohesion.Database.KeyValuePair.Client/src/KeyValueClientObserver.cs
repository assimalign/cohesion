using System;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// A telemetry hook invoked around every command a connection executes. Derive from it, override the
/// hooks you need, and compose it on <see cref="KeyValueClientOptions.Observer"/> to record command
/// timing, throughput, and failures.
/// </summary>
/// <remarks>
/// <para>
/// The hooks run synchronously on the executing path. Each has an empty body, so an observer overrides
/// only what it records. They are <c>protected internal</c>: the owning
/// <see cref="KeyValueConnection"/> fires them, and code outside this assembly reaches them only by
/// overriding. An observer therefore cannot forward to another observer instance (CS1540); an
/// application with several sinks fans out inside one subclass. A hook that throws does not fault the
/// command it observes, nor mask its failure: the connection discards the observer's exception (an
/// <see cref="OutOfMemoryException"/> still propagates).
/// </para>
/// <para>
/// The hooks take primitives (not event objects), so instrumenting a client allocates nothing per
/// command and stays AOT- and trimming-clean. The command text is the grammar form (for example
/// <c>PUT @k @v IF @etag</c>): key and value bytes never reach the observer. The constructor is
/// protected: the application supplies the observer and KeyValuePair.Client calls it.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class KeyValueClientObserver
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeyValueClientObserver"/> class.
    /// </summary>
    protected KeyValueClientObserver()
    {
    }

    /// <summary>
    /// Invoked immediately before a command is sent to the server.
    /// </summary>
    /// <param name="commandText">The command's grammar text.</param>
    /// <param name="parameterCount">The number of bound parameters.</param>
    protected internal virtual void OnExecuting(string commandText, int parameterCount)
    {
    }

    /// <summary>
    /// Invoked after a command completes successfully.
    /// </summary>
    /// <param name="commandText">The command's grammar text.</param>
    /// <param name="rowCount">The number of rows returned, or 0 for non-row-returning commands.</param>
    /// <param name="affectedCount">The number of entries affected, or -1 for row-returning commands.</param>
    /// <param name="elapsed">The wall-clock time the command took.</param>
    protected internal virtual void OnExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed)
    {
    }

    /// <summary>
    /// Invoked when a command fails.
    /// </summary>
    /// <param name="commandText">The command's grammar text.</param>
    /// <param name="exception">The failure, carrying its key-value error kind and wire code.</param>
    /// <param name="elapsed">The wall-clock time elapsed before the failure surfaced.</param>
    protected internal virtual void OnFailed(string commandText, KeyValueClientException exception, TimeSpan elapsed)
    {
    }
}
