using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Internal;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of a session whose model has a statement language: statement text in the model's
/// grammar is a second entry point beside the typed request. Its typed requests are what its
/// parser produces, so a language model always has both capabilities and refuses neither.
/// </summary>
/// <typeparam name="TRequest">The model's request root: the request type its parser produces and its typed execute member takes.</typeparam>
/// <remarks>
/// <para>
/// <b>The text seam is the model's own.</b> <see cref="ExecuteAsync(string, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>
/// parses the text in the session's language (SQL, OQL, GQL) and executes it; a parse failure
/// throws <see cref="DatabaseParseException"/>. A caller that holds the typed leaf reaches both
/// members: <c>session.ExecuteAsync("…")</c> binds to the text member and
/// <c>session.ExecuteAsync(request)</c> to the typed forwarder. A model without a statement
/// language does not derive this base, so it has no text member to refuse.
/// </para>
/// <para>
/// <b>Shape.</b> The text member is non-virtual and runs the checks every model shares (the closed
/// session, blank text, the canceled token) and the statement trace before it calls
/// <see cref="ExecuteCoreAsync(string, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>.
/// A text core that parses and then runs the typed member (Graph, Documents) writes one statement,
/// for the outermost call. The text prologue is a copy of the typed level's, around the other core,
/// with a null request (the trace's <c>requestKind</c> is <c>Text</c>); the tail is the
/// non-generic <see cref="DatabaseSession"/>'s. The leaves live in the model assemblies, so the
/// constructor is <c>protected</c>.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseLanguageSession<TRequest> : DatabaseExecutionSession<TRequest>
    where TRequest : DatabaseRequest
{
    /// <summary>
    /// Initializes a new session that executes typed requests and statement text, scoped to a
    /// database.
    /// </summary>
    /// <param name="database">The database the session is scoped to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    protected DatabaseLanguageSession(DatabaseInstance database)
        : base(database)
    {
    }

    // Keep this typed forwarder before the text overload. For a failed call Roslyn reports the
    // first-declared candidate of the most-derived type, so a foreign request names TRequest.
    // Forward through the public NVI, never the core: its validation and tracing must still run.
    /// <summary>Executes a typed request belonging to this session's model.</summary>
    /// <param name="request">The model's request.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The request's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseException">The session is closed, or the request failed.</exception>
    public new ValueTask<QueryResult> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default)
        => base.ExecuteAsync(request, cancellationToken);

    /// <summary>
    /// Executes statement text in the session's own language, binding the given named parameter
    /// values.
    /// </summary>
    /// <param name="statement">The statement text to parse and execute.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null when the statement takes none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    /// <exception cref="ArgumentException"><paramref name="statement"/> is null, empty or white space.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseParseException">The statement text failed to parse in the session's language.</exception>
    /// <exception cref="DatabaseException">The session is closed, or the statement failed.</exception>
    public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        cancellationToken.ThrowIfCancellationRequested();
        if (!DatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None) || _tracingStatement)
        {
            return ExecuteCoreAsync(statement, parameters, cancellationToken);
        }

        return ExecuteTraced(statement, parameters, cancellationToken);
    }

    /// <summary>
    /// Parses and executes statement text on an open session.
    /// </summary>
    /// <param name="statement">The statement text; never null, empty or white space.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The query result.</returns>
    protected abstract ValueTask<QueryResult> ExecuteCoreAsync(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    // The traced statement text: the typed level's prologue (DatabaseExecutionSession<TRequest>)
    // around the text core, with a null request, so the trace reports the Text request kind. The
    // two copies stay in step; DatabaseEventSourceTests pins both paths' allocations against their
    // cores.
    private ValueTask<QueryResult> ExecuteTraced(string statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        _tracingStatement = true;
        ValueTask<QueryResult> pending;
        Exception? thrown = null;
        bool startWritten = false;
        try
        {
            startWritten = DatabaseEventSource.Log.StatementStart(this, request: null);
            pending = ExecuteCoreAsync(statement, parameters, cancellationToken);
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
                DatabaseEventSource.Log.StatementThrew(this, request: null, thrown, startWritten, started);
            }
        }

        return CompleteTraced(pending, request: null, startWritten, started);
    }
}
