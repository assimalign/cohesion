using System;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// A telemetry hook invoked around every command a connection executes. Derive from it, override the
/// hooks you need, and compose it on <see cref="SqlClientOptions.Observer"/> to record command timing,
/// throughput, and failures.
/// </summary>
/// <remarks>
/// <para>
/// The hooks run synchronously on the executing path. Each has an empty body, so an observer overrides
/// only what it records. They are <c>protected internal</c>: the owning <see cref="SqlConnection"/> fires
/// them, and code outside this assembly reaches them only by overriding. An observer therefore cannot
/// forward to another observer instance (CS1540); an application with several sinks fans out inside one
/// subclass. A hook that throws does not fault the command it observes, nor mask its failure: the
/// connection discards the observer's exception (an <see cref="OutOfMemoryException"/> still
/// propagates).
/// </para>
/// <para>
/// The hooks take primitives (not event objects), so instrumenting a client allocates nothing per
/// command and stays AOT- and trimming-clean. The constructor is protected: the application supplies
/// the observer and Sql.Client calls it.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class SqlClientObserver
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlClientObserver"/> class.
    /// </summary>
    protected SqlClientObserver()
    {
    }

    /// <summary>
    /// Invoked immediately before a command is sent to the server.
    /// </summary>
    /// <param name="commandText">The command's statement text.</param>
    /// <param name="parameterCount">The number of bound parameters.</param>
    protected internal virtual void OnExecuting(string commandText, int parameterCount)
    {
    }

    /// <summary>
    /// Invoked after a command completes successfully.
    /// </summary>
    /// <param name="commandText">The command's statement text.</param>
    /// <param name="rowCount">The number of rows returned, or 0 for non-row-returning commands.</param>
    /// <param name="affectedCount">The number of records affected, or -1 for row-returning commands.</param>
    /// <param name="elapsed">The wall-clock time the command took.</param>
    protected internal virtual void OnExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed)
    {
    }

    /// <summary>
    /// Invoked when a command fails.
    /// </summary>
    /// <param name="commandText">The command's statement text.</param>
    /// <param name="exception">The failure, carrying its SQL error kind and wire code.</param>
    /// <param name="elapsed">The wall-clock time elapsed before the failure surfaced.</param>
    protected internal virtual void OnFailed(string commandText, SqlClientException exception, TimeSpan elapsed)
    {
    }
}
