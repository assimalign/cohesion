using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A database whose cores create <see cref="TestSession"/>s, apply a schema when it supports
/// provisioning, and record their disposal.
/// </summary>
internal sealed class TestDatabase : DatabaseInstance
{
    private int _sessions;
    private int _disposeCores;
    private int _asyncDisposeCores;
    private int _schemaApplies;

    public TestDatabase(DatabaseName name, DatabaseEngine engine, bool supportsSchemaProvisioning = false, TestLog? log = null)
        : base(name, engine, supportsSchemaProvisioning)
    {
        Log = log ?? new TestLog();
    }

    public TestLog Log { get; }

    public int Sessions => Volatile.Read(ref _sessions);

    public int DisposeCores => Volatile.Read(ref _disposeCores);

    public int AsyncDisposeCores => Volatile.Read(ref _asyncDisposeCores);

    public int SchemaApplies => Volatile.Read(ref _schemaApplies);

    public bool Disposed => IsDisposed;

    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sessions);
        return new ValueTask<DatabaseSession>(new TestSession(this, Log));
    }

    protected override ValueTask<SchemaMigrationResult> ApplySchemaCoreAsync(CompiledSchema schema, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _schemaApplies);
        return new ValueTask<SchemaMigrationResult>(new SchemaMigrationResult(null, schema.Hash, 1, false));
    }

    protected override void DisposeCore()
    {
        Interlocked.Increment(ref _disposeCores);
        Log.Add($"database:{Name}:dispose");
    }

    protected override ValueTask DisposeAsyncCore()
    {
        Interlocked.Increment(ref _asyncDisposeCores);
        Log.Add($"database:{Name}:dispose");
        return ValueTask.CompletedTask;
    }
}
