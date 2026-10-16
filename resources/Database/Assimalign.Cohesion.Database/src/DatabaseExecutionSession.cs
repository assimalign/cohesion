using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Internal;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of a session whose model executes typed requests. The type argument is the model's
/// request root, so a request of another model does not compile.
/// </summary>
/// <typeparam name="TRequest">The model's request root: the only request type the session executes.</typeparam>
/// <remarks>
/// <para>
/// <b>One capability per level.</b> The typed execute member is the only execute member this level
/// declares. A model whose requests are typed operations and which has no statement language (the
/// key-value model) derives this base directly. A model with a statement language derives
/// <see cref="DatabaseLanguageSession{TRequest}"/>, which adds statement text. No model inherits an
/// execute member it must refuse, and no session checks whether a request belongs to its model: a
/// request of another model is a compile error.
/// </para>
/// <para>
/// <b>Shape.</b> <see cref="ExecuteAsync(TRequest, CancellationToken)"/> is non-virtual and runs
/// the checks every model shares (the closed session, the null request, the canceled token) and
/// the statement trace (event-sources plan, D5 a) before it calls
/// <see cref="ExecuteCoreAsync(TRequest, CancellationToken)"/>. The leaves live in the model
/// assemblies, so the constructor is <c>protected</c>. The trace's synchronous prologue lives here;
/// its tail is the non-generic <see cref="DatabaseSession"/>'s, so every model shares one pooled
/// async box type. The type argument is always a reference type, so NativeAOT shares one canonical
/// body per member, and no member is a generic virtual method.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseExecutionSession<TRequest> : DatabaseSession
    where TRequest : DatabaseRequest
{
    /// <summary>
    /// Initializes a new session that executes typed requests, scoped to a database.
    /// </summary>
    /// <param name="database">The database the session is scoped to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    protected DatabaseExecutionSession(DatabaseInstance database)
        : base(database)
    {
    }

    /// <summary>
    /// Executes a typed request of the session's model against the database.
    /// </summary>
    /// <param name="request">The request to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseException">The session is closed, or the request failed.</exception>
    public ValueTask<QueryResult> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // The statement path's fast path (event-sources plan, D5 a): the core is returned as it is
        // while nobody listens, and while an outer statement of this session is already traced.
        if (!DatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None) || _tracingStatement)
        {
            return ExecuteCoreAsync(request, cancellationToken);
        }

        return ExecuteTraced(request, cancellationToken);
    }

    /// <summary>
    /// Executes a typed request on an open session.
    /// </summary>
    /// <param name="request">The request; never null.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The query result.</returns>
    protected abstract ValueTask<QueryResult> ExecuteCoreAsync(TRequest request, CancellationToken cancellationToken);

    // The traced typed request (event-sources plan, D5 a and §4.1 "Re-entry"): entered only while
    // the source is enabled, for the outermost statement on the session; its timestamp costs nothing
    // while nobody listens. It calls the core exactly as the untraced path does, so a core that
    // throws synchronously throws from the call whether or not anyone listens (event-source.md,
    // rule 12). A failure is captured by the filter, which catches nothing, and written from the
    // finally, once the core's own finally blocks released what they held. The request is handed to
    // the event source and to the shared tail as a DatabaseRequest, so the prologue needs no
    // generic-dictionary lookup. DatabaseLanguageSession<TRequest> carries the text prologue's copy.
    private ValueTask<QueryResult> ExecuteTraced(TRequest request, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        _tracingStatement = true;
        ValueTask<QueryResult> pending;
        Exception? thrown = null;
        bool startWritten = false;
        try
        {
            startWritten = DatabaseEventSource.Log.StatementStart(this, request);
            pending = ExecuteCoreAsync(request, cancellationToken);
        }
        catch (Exception exception) when (DatabaseEventSource.CaptureFailure(exception, out thrown))
        {
            throw;
        }
        finally
        {
            if (thrown is not null)
            {
                _tracingStatement = false;
                DatabaseEventSource.Log.StatementThrew(this, request, thrown, startWritten, started);
            }
        }

        return CompleteTraced(pending, request, startWritten, started);
    }
}
