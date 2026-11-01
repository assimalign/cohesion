using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Prevents session DDL from interleaving with a complete schema apply. Ordinary conflicting
/// admission refuses immediately; sequence DDL and fully reserved apply steps may wait for
/// metadata after their object grants. Definition leases never span object-lock waits.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlDdlInterlock")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDdlInterlock.DebugView))]
internal sealed class SqlDdlInterlock
{
    private readonly object _sync = new();
    private string? _schema;
    private int _statements;
    private int _metadataStatements;
    private bool _definitionStatement;
    private TaskCompletionSource? _metadataChanged;

    // A definition can refuse conflicting admission before reading the catalog, without
    // owning a lease during snapshot capture or any subsequent transaction-lock wait.
    internal void CheckDefinitionAdmission(string database)
    {
        lock (_sync)
        {
            if (_metadataStatements != 0)
            {
                throw new DatabaseObjectLockedException(database, database, "SQL definition DDL");
            }
        }
    }

    // A definition drop checks persisted dependencies against a stable metadata directory.
    // Unrelated table DDL remains concurrent; conflicting definition DDL refuses immediately.
    internal IDisposable EnterMetadata(string database, bool definition)
    {
        lock (_sync)
        {
            if (_definitionStatement || definition && _metadataStatements != 0)
            {
                throw new DatabaseObjectLockedException(database, database, "SQL definition DDL");
            }
            _metadataStatements++;
            _definitionStatement = definition;
            return new MetadataLease(this);
        }
    }

    // All sequence/schema object grants precede this lease. Definition DDL never waits
    // for an object grant under its lease, so this wait cannot close a hidden cycle.
    internal static Action<CancellationTokenSource?>? MetadataWaitStarting { get; set; }

    internal async ValueTask<IDisposable> EnterMetadataAsync(bool definition, CancellationToken cancellationToken,
        TimeSpan lockTimeout = default)
    {
        CancellationTokenSource? timeout = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                timeout?.Token.ThrowIfCancellationRequested();
                Task changed;
                lock (_sync)
                {
                    if (!_definitionStatement && (!definition || _metadataStatements == 0))
                    {
                        _metadataStatements++;
                        _definitionStatement = definition;
                        return new MetadataLease(this);
                    }
                    changed = (_metadataChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }
                if (timeout is null && lockTimeout != TimeSpan.Zero)
                {
                    timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(lockTimeout);
                }
                MetadataWaitStarting?.Invoke(timeout);
                await changed.WaitAsync(timeout?.Token ?? cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout?.IsCancellationRequested == true)
        { throw SqlTimeoutException.Lock(exception, null); }
        finally { timeout?.Dispose(); }
    }

    [global::System.Diagnostics.DebuggerDisplay("MetadataLease")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDdlInterlock.MetadataLease.DebugView))]
    private sealed class MetadataLease : IDisposable
    {
        private SqlDdlInterlock? _owner;
        internal MetadataLease(SqlDdlInterlock owner) { _owner = owner; }
        public void Dispose()
        {
            if (_owner is not { } owner) { return; }
            lock (owner._sync)
            {
                owner._metadataStatements--;
                owner._definitionStatement = false;
                owner._metadataChanged?.TrySetResult();
                owner._metadataChanged = null;
                _owner = null;
            }
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDdlInterlock.MetadataLease value)
            {
                _ = value;
            }
        }
    }

    internal IDisposable EnterApply(string schema)
    {
        lock (_sync)
        {
            if (_schema is not null || _statements != 0)
            {
                throw new DatabaseObjectLockedException(schema, _schema ?? schema, "APPLY SCHEMA");
            }
            _schema = schema;
            return new Lease(this, apply: true);
        }
    }

    internal IDisposable EnterStatement(string database)
    {
        lock (_sync)
        {
            if (_schema is not null)
            {
                throw new DatabaseObjectLockedException(database, _schema, "DDL during APPLY SCHEMA");
            }
            _statements++;
            return new Lease(this, apply: false);
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("Lease")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDdlInterlock.Lease.DebugView))]
    private sealed class Lease : IDisposable
    {
        private SqlDdlInterlock? _owner;
        private readonly bool _apply;

        internal Lease(SqlDdlInterlock owner, bool apply)
        {
            _owner = owner;
            _apply = apply;
        }

        public void Dispose()
        {
            if (_owner is not { } owner)
            {
                return;
            }
            lock (owner._sync)
            {
                if (_apply)
                {
                    owner._schema = null;
                }
                else
                {
                    owner._statements--;
                }
                _owner = null;
            }
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDdlInterlock.Lease value)
            {
                _ = value;
            }
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDdlInterlock value)
        {
            _ = value;
        }
    }
}
