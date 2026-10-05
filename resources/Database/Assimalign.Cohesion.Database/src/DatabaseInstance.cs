using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every logical database an engine manages: a shared handle that creates sessions
/// and, for a model that supports it, applies a compiled schema.
/// </summary>
/// <remarks>
/// <para>
/// The name, the owning engine and the schema-provisioning capability are fixed by the protected
/// constructor and read without a virtual call. A leaf re-exposes its typed engine with a
/// <c>new</c> property over a typed field of its own (<c>public new SqlDatabaseEngine Engine</c>),
/// and its typed session factory with a <c>new</c> member that awaits
/// <see cref="CreateSessionAsync"/>, never <see cref="CreateSessionCoreAsync"/>.
/// </para>
/// <para>
/// <b>Schema provisioning is the one capability</b> (row 8 of the concrete-types plan):
/// <see cref="SupportsSchemaProvisioning"/> is fixed at construction, and
/// <see cref="ApplySchemaAsync"/> throws <see cref="NotSupportedException"/> while it is false. A
/// model that provisions schemas passes <c>true</c> and overrides
/// <see cref="ApplySchemaCoreAsync"/>.
/// </para>
/// <para>
/// <b>Disposal is idempotent and the base owns the flag</b>: the first <see cref="Dispose"/> or
/// <see cref="DisposeAsync"/> runs the leaf's <see cref="DisposeCore"/> or
/// <see cref="DisposeAsyncCore"/>, and every later call returns. Disposing a database closes it;
/// the engine that manages it disposes it when the database is dropped, reopened after going
/// offline, or when the engine is disposed.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> The leaves live in the model assemblies,
/// so the constructor is <c>protected</c>. The name is <c>DatabaseInstance</c>, never
/// <c>Database</c>, so code in a namespace such as <c>Acme.Database</c> can name it (D4). Until
/// phase 6 the base also implements <see cref="IDatabase"/>; it does not implement
/// <see cref="IDatabaseSchemaProvisioner"/>, which only the SQL leaf lists until phase 6.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseInstance : IDatabase
{
    private readonly DatabaseName _name;
    private readonly DatabaseEngine _engine;
    private readonly bool _supportsSchemaProvisioning;
    private int _disposed;

    /// <summary>
    /// Initializes a new database with its name, its owning engine and its capability.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="engine">The engine that owns the database.</param>
    /// <param name="supportsSchemaProvisioning">
    /// Whether <see cref="ApplySchemaAsync"/> is supported; a leaf that passes true overrides
    /// <see cref="ApplySchemaCoreAsync"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    protected DatabaseInstance(DatabaseName name, DatabaseEngine engine, bool supportsSchemaProvisioning = false)
    {
        if (name.IsEmpty)
        {
            throw new ArgumentException("A database name is required.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(engine);
        _name = name;
        _engine = engine;
        _supportsSchemaProvisioning = supportsSchemaProvisioning;
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
    /// Gets whether the database applies compiled schemas (<see cref="ApplySchemaAsync"/>).
    /// </summary>
    public bool SupportsSchemaProvisioning => _supportsSchemaProvisioning;

    /// <summary>
    /// Gets whether the database has been disposed.
    /// </summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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
    /// Diffs the database against a compiled schema and applies the difference.
    /// </summary>
    /// <param name="schema">The desired schema.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The migration result.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="NotSupportedException"><see cref="SupportsSchemaProvisioning"/> is false.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    public ValueTask<SchemaMigrationResult> ApplySchemaAsync(CompiledSchema schema, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(schema);
        if (!_supportsSchemaProvisioning)
        {
            throw new NotSupportedException($"Database '{_name}' of the {_engine.Model} model does not apply compiled schemas.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ApplySchemaCoreAsync(schema, cancellationToken);
    }

    /// <summary>
    /// Closes the database. Idempotent: only the first call reaches <see cref="DisposeCore"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeCore();
    }

    /// <summary>
    /// Closes the database. Idempotent: only the first call reaches <see cref="DisposeAsyncCore"/>.
    /// </summary>
    /// <returns>A task that completes once the database is closed.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return DisposeAsyncCore();
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
    /// Applies a compiled schema on a database that supports it. The default throws
    /// <see cref="NotSupportedException"/>: a leaf that passes <c>supportsSchemaProvisioning: true</c>
    /// to the constructor overrides it.
    /// </summary>
    /// <param name="schema">The desired schema; never null.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The migration result.</returns>
    /// <exception cref="NotSupportedException">The leaf does not provision schemas.</exception>
    protected virtual ValueTask<SchemaMigrationResult> ApplySchemaCoreAsync(CompiledSchema schema, CancellationToken cancellationToken)
        => throw new NotSupportedException($"Database '{_name}' of the {_engine.Model} model does not apply compiled schemas.");

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

    IDatabaseEngine IDatabase.Engine => _engine;

    async ValueTask<IDatabaseSession> IDatabase.CreateSessionAsync(CancellationToken cancellationToken)
        => await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
}
