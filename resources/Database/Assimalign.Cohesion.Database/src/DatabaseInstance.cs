using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every logical database an engine manages: a shared handle that creates sessions.
/// </summary>
/// <remarks>
/// <para>
/// The name and the owning engine are fixed by the protected constructor and read without a
/// virtual call. A leaf re-exposes its typed engine with a <c>new</c> property over a typed field
/// of its own (<c>public new SqlDatabaseEngine Engine</c>), and its typed session factory with a
/// <c>new</c> member that awaits <see cref="CreateSessionAsync"/>, never
/// <see cref="CreateSessionCoreAsync"/>.
/// </para>
/// <para>
/// <b>No capability member</b> (owner decision 50 of 2026-10-09). The base used to carry one,
/// schema provisioning: a <c>SupportsSchemaProvisioning</c> flag and an <c>ApplySchemaAsync</c>
/// member over a model-agnostic compiled schema. Provisioning belongs to the model that owns the
/// schema's shape: a SQL engine provisions the databases its builder declares while it is built,
/// and <c>SqlDatabase.ApplySchemaAsync</c> applies a SQL compiled schema imperatively.
/// </para>
/// <para>
/// <b>Disposal is idempotent and the base owns the flag</b>: the first <see cref="Dispose"/> or
/// <see cref="DisposeAsync"/> runs the leaf's <see cref="DisposeCore"/> or
/// <see cref="DisposeAsyncCore"/>, and a later call waits until that close has ended, then
/// returns. Disposing a database closes it; the engine that manages it disposes it when the
/// database is dropped, reopened after going offline, or when the engine is disposed.
/// </para>
/// <para>
/// <b>A database closed outside its engine is forgotten</b> (owner decision 33 of 2026-10-06,
/// #1289): when a close ends, whoever ran it, the base hands the database to its engine
/// (<see cref="DatabaseEngine.ForgetClosedDatabaseCore"/>), and the engine stops tracking it if
/// it still does, so a later <see cref="DatabaseEngine.OpenDatabaseAsync"/> opens the database
/// again from its files. An open that finds the database still closing waits for the close to
/// end first, so the reopen never races the close for the database's files. A close the engine
/// ran itself (a drop, a reopen after going offline, the engine's disposal) is unchanged: the
/// engine had already let the database go.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> The leaves live in the model assemblies,
/// so the constructor is <c>protected</c>. The name is <c>DatabaseInstance</c>, never
/// <c>Database</c>, so code in a namespace such as <c>Acme.Database</c> can name it (D4).
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseInstance : IAsyncDisposable, IDisposable
{
    private readonly DatabaseName _name;
    private readonly DatabaseEngine _engine;

    // Completed once the first close ran to its end and the engine was told (CompleteClose). A
    // later Dispose or DisposeAsync, and an engine open that found the database closing, wait on it.
    private readonly TaskCompletionSource _closure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    /// <summary>
    /// Initializes a new database with its name and its owning engine.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="engine">The engine that owns the database.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    protected DatabaseInstance(DatabaseName name, DatabaseEngine engine)
    {
        if (name.IsEmpty)
        {
            throw new ArgumentException("A database name is required.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(engine);
        _name = name;
        _engine = engine;
    }

    /// <summary>
    /// Gets the name of this database.
    /// </summary>
    public DatabaseName Name => _name;

    /// <summary>
    /// Gets the engine that owns this database.
    /// </summary>
    public DatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets whether the database has been disposed: true from the moment its close starts.
    /// </summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Gets whether the database's close has started, for the engine base's open and lookup.
    /// </summary>
    internal bool IsClosing => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Gets the task that completes once the database's close has ended and its engine was told,
    /// for the engine base's open.
    /// </summary>
    internal Task Closure => _closure.Task;

    /// <summary>
    /// Creates a new lightweight session scoped to this database.
    /// </summary>
    /// <param name="cancellationToken">Observed before the session is created.</param>
    /// <returns>A new session.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    public ValueTask<DatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return CreateSessionCoreAsync(cancellationToken);
    }

    /// <summary>
    /// Closes the database. Idempotent: only the first call reaches <see cref="DisposeCore"/>, and
    /// a call made while another caller's close runs blocks until that close has ended.
    /// </summary>
    /// <remarks>
    /// When the close ends, the database's engine forgets it if it still tracks it (owner decision
    /// 33, #1289), so a later <see cref="DatabaseEngine.OpenDatabaseAsync"/> reopens it from its
    /// files.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            // Another caller's close: the engine disposes a database it let go (a drop, a reopen
            // after going offline) and then reuses its files, so it must not run ahead of a close a
            // holder started. A close never disposes its own database again, so this never waits
            // on itself.
            _closure.Task.GetAwaiter().GetResult();
            return;
        }

        try
        {
            DisposeCore();
        }
        finally
        {
            CompleteClose();
        }
    }

    /// <summary>
    /// Closes the database. Idempotent: only the first call reaches <see cref="DisposeAsyncCore"/>,
    /// and a call made while another caller's close runs completes once that close has ended.
    /// </summary>
    /// <returns>A task that completes once the database is closed.</returns>
    /// <remarks>
    /// When the close ends, the database's engine forgets it if it still tracks it (owner decision
    /// 33, #1289), so a later <see cref="DatabaseEngine.OpenDatabaseAsync"/> reopens it from its
    /// files.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return new ValueTask(_closure.Task);
        }

        return CloseAsync();
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> once the database has been disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Creates a session on a database that <see cref="CreateSessionAsync"/> found open.
    /// </summary>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>A new session.</returns>
    protected abstract ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes the database synchronously. Called once, by the first <see cref="Dispose"/>, unless
    /// <see cref="DisposeAsync"/> ran first.
    /// </summary>
    protected abstract void DisposeCore();

    /// <summary>
    /// Closes the database. Called once, by the first <see cref="DisposeAsync"/>, unless
    /// <see cref="Dispose"/> ran first.
    /// </summary>
    /// <returns>A task that completes once the database is closed.</returns>
    protected abstract ValueTask DisposeAsyncCore();

    // The first DisposeAsync's close: the leaf's core, then the engine is told, whatever the core threw.
    private async ValueTask CloseAsync()
    {
        try
        {
            await DisposeAsyncCore().ConfigureAwait(false);
        }
        finally
        {
            CompleteClose();
        }
    }

    // Runs once, when the first close ended: the engine forgets the database before anyone waiting
    // for the close resumes, so an open that waited finds it gone and opens it again from its files.
    private void CompleteClose()
    {
        try
        {
            _engine.ForgetClosedDatabase(this);
        }
        finally
        {
            _closure.TrySetResult();
        }
    }
}
