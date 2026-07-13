using System;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// A telemetry hook invoked around every command a connection executes. Compose one
/// on <see cref="SqlClientOptions.Observer"/> to record command timing, throughput,
/// and failures.
/// </summary>
/// <remarks>
/// Callbacks run synchronously on the executing path and must not throw — an
/// observer failure would fault an otherwise successful command. The surface takes
/// primitives (not event objects) so instrumenting a client allocates nothing per
/// command, keeping it AOT- and trimming-clean.
/// </remarks>
public interface ISqlClientObserver
{
    /// <summary>
    /// Invoked immediately before a command is sent to the server.
    /// </summary>
    /// <param name="commandText">The command's statement text.</param>
    /// <param name="parameterCount">The number of bound parameters.</param>
    void OnExecuting(string commandText, int parameterCount);

    /// <summary>
    /// Invoked after a command completes successfully.
    /// </summary>
    /// <param name="commandText">The command's statement text.</param>
    /// <param name="rowCount">The number of rows returned, or 0 for non-row-returning commands.</param>
    /// <param name="affectedCount">The number of records affected, or -1 for row-returning commands.</param>
    /// <param name="elapsed">The wall-clock time the command took.</param>
    void OnExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed);

    /// <summary>
    /// Invoked when a command fails.
    /// </summary>
    /// <param name="commandText">The command's statement text.</param>
    /// <param name="exception">The failure, carrying its SQL error kind and wire code.</param>
    /// <param name="elapsed">The wall-clock time elapsed before the failure surfaced.</param>
    void OnFailed(string commandText, SqlClientException exception, TimeSpan elapsed);
}
