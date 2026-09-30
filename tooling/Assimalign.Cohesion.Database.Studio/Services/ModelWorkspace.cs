using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Client;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Client;
using Assimalign.Cohesion.Database.KeyValuePair.Client;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// The state every model page shares: which database is current, the embedded session (embedded
/// mode) or the client connection (wire modes), database management on the in-process engine, and
/// explicit transactions for embedded sessions. All operations are serialized through one gate
/// because an engine session runs one operation at a time.
/// </summary>
internal abstract class ModelWorkspace : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected ModelWorkspace(StudioModel model, ConnectionMode mode, StudioEngines engines, EndPoint? wireEndPoint)
    {
        Model = model;
        Mode = mode;
        Engines = engines;
        WireEndPoint = wireEndPoint;
    }

    public StudioModel Model { get; }

    public ConnectionMode Mode { get; }

    public StudioEngines Engines { get; }

    public IDatabaseEngine Engine => Engines.Get(Model);

    public EndPoint? WireEndPoint { get; }

    public string? CurrentDatabase { get; private set; }

    /// <summary>The embedded session; null in wire modes.</summary>
    public IDatabaseSession? Session { get; private set; }

    /// <summary>Database create/drop/list needs the engine; an external server exposes no such verbs over the wire.</summary>
    public bool CanManageDatabases => Mode != ConnectionMode.WireExternal;

    public virtual bool SupportsSessionTransactions => Mode == ConnectionMode.Embedded;

    protected abstract string ClientName { get; }

    public string Description => Mode switch
    {
        ConnectionMode.Embedded => $"Embedded: in-process {Engine.GetType().Name} + IDatabaseSession",
        ConnectionMode.WireLoopback => $"Wire (loopback {EndPointText}): Studio-owned server + {ClientName}",
        ConnectionMode.WireExternal => $"Wire (external {EndPointText}): {ClientName}",
        _ => Mode.ToString(),
    };

    private string EndPointText => WireEndPoint switch
    {
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        null => "(none)",
        { } other => other.ToString() ?? string.Empty,
    };

    public bool InTransaction => TransactionText is not null;

    /// <summary>Null when no transaction is active; otherwise a short description.</summary>
    public virtual string? TransactionText => Session?.CurrentTransaction is { State: TransactionState.Active } transaction
        ? $"{transaction.IsolationLevel} {transaction.Id}"
        : null;

    public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        => RunExclusiveAsync<bool>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Lists the engine's databases. SQL and Key-Value only enumerate databases opened in this
    /// process, so <paramref name="discoverOnDisk"/> also tries to open each folder under the model root.
    /// </summary>
    public Task<IReadOnlyList<string>> ListDatabasesAsync(bool discoverOnDisk, CancellationToken cancellationToken = default)
        => RunExclusiveAsync<IReadOnlyList<string>>(async token =>
        {
            if (!CanManageDatabases)
            {
                return CurrentDatabase is null ? [] : [CurrentDatabase];
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (IDatabase database in Engine.GetDatabasesAsync(token).ConfigureAwait(false))
            {
                names.Add(database.Name.ToString());
            }

            if (discoverOnDisk)
            {
                string root = Engines.GetModelRoot(Model);
                if (Directory.Exists(root))
                {
                    foreach (string directory in Directory.EnumerateDirectories(root))
                    {
                        string name = System.IO.Path.GetFileName(directory);
                        if (names.Contains(name) || name.EndsWith(".catalog", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            await Engine.OpenDatabaseAsync(name, token).ConfigureAwait(false);
                            names.Add(name);
                        }
                        catch (Exception)
                        {
                            // Not a database folder for this engine.
                        }
                    }
                }
            }

            return [.. names];
        }, cancellationToken);

    public Task CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            RequireManagement();
            await Engine.CreateDatabaseAsync(name.Trim(), token).ConfigureAwait(false);
        }, cancellationToken);

    public Task DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            RequireManagement();
            if (string.Equals(CurrentDatabase, name, StringComparison.OrdinalIgnoreCase))
            {
                await CloseCurrentAsync().ConfigureAwait(false);
            }

            await Engine.DropDatabaseAsync(name, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Makes <paramref name="name"/> the current database: opens a session (embedded) or connects the client (wire).</summary>
    public Task UseDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            name = name.Trim();
            await CloseCurrentAsync().ConfigureAwait(false);

            if (Mode == ConnectionMode.Embedded)
            {
                IDatabase database = await Engine.OpenDatabaseAsync(name, token).ConfigureAwait(false);
                Session = await database.CreateSessionAsync(token).ConfigureAwait(false);
            }
            else
            {
                if (Mode == ConnectionMode.WireLoopback)
                {
                    // The wire carries no database-management verbs; make sure it exists in the engine first.
                    await Engine.OpenDatabaseAsync(name, token).ConfigureAwait(false);
                }

                await OpenWireAsync(name, WireEndPoint ?? throw new InvalidOperationException("No wire endpoint."), token).ConfigureAwait(false);
            }

            CurrentDatabase = name;
        }, cancellationToken);

    public Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode != ConnectionMode.Embedded)
            {
                await BeginWireTransactionAsync(token).ConfigureAwait(false);
                return;
            }

            await RequireSession().BeginTransactionAsync(isolationLevel, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task CommitAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode != ConnectionMode.Embedded)
            {
                await EndWireTransactionAsync(commit: true, token).ConfigureAwait(false);
                return;
            }

            IDatabaseTransaction transaction = RequireSession().CurrentTransaction
                ?? throw new InvalidOperationException("No transaction is active.");
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            if (Mode != ConnectionMode.Embedded)
            {
                await EndWireTransactionAsync(commit: false, token).ConfigureAwait(false);
                return;
            }

            IDatabaseTransaction transaction = RequireSession().CurrentTransaction
                ?? throw new InvalidOperationException("No transaction is active.");
            await transaction.RollbackAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    protected virtual Task BeginWireTransactionAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException($"{Model.DisplayName} has no wire transaction verbs; explicit transactions are embedded-only.");

    protected virtual Task EndWireTransactionAsync(bool commit, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{Model.DisplayName} has no wire transaction verbs; explicit transactions are embedded-only.");

    /// <summary>Opens a short-lived embedded session on the current database (loopback/embedded only).</summary>
    protected async Task<IDatabaseSession> OpenAdminSessionAsync(CancellationToken cancellationToken)
    {
        if (!CanManageDatabases)
        {
            throw new NotSupportedException("An external server exposes no catalog/admin verbs for this model over the wire.");
        }

        IDatabase database = await Engine.OpenDatabaseAsync(RequireDatabase(), cancellationToken).ConfigureAwait(false);
        return await database.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    protected abstract Task OpenWireAsync(string database, EndPoint endPoint, CancellationToken cancellationToken);

    protected abstract ValueTask CloseWireAsync();

    protected IDatabaseSession RequireSession()
        => Session ?? throw new InvalidOperationException("Select a database first.");

    protected string RequireDatabase()
        => CurrentDatabase ?? throw new InvalidOperationException("Select a database first.");

    private void RequireManagement()
    {
        if (!CanManageDatabases)
        {
            throw new NotSupportedException("Database create/drop/list is engine-side only; the external wire exposes no database-management verbs.");
        }
    }

    private async Task CloseCurrentAsync()
    {
        if (Session is { } session)
        {
            Session = null;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        try
        {
            await CloseWireAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        CurrentDatabase = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Settings shared by every wire client: one pinned connection, anonymous principal.</summary>
    protected static DatabaseConnectionSettings WireSettings(string database, EndPoint endPoint) => new()
    {
        Database = database,
        Principal = "studio",
        EndPoint = endPoint,
        MaxPoolSize = 2,
    };
}

/// <summary>Human-readable error text, including client error kinds/codes and inner exceptions.</summary>
internal static class ErrorText
{
    public static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        Append(builder, exception, 0);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, Exception exception, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        if (depth > 0)
        {
            builder.AppendLine().Append(' ', depth * 2).Append("-> ");
        }

        builder.Append(exception.GetType().Name);
        string? code = exception switch
        {
            SqlClientException sql => $"{sql.Kind}/{sql.Code}",
            GraphClientException graph => $"{graph.Code}",
            KeyValueClientException keyValue => $"{keyValue.Kind}/{keyValue.Code}",
            BlobClientException blob => $"{blob.Code}",
            DatabaseClientException client => $"{client.Code}",
            _ => null,
        };

        if (code is not null)
        {
            builder.Append(" [").Append(code).Append(']');
        }

        builder.Append(": ").Append(exception.Message);

        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                Append(builder, inner, depth + 1);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            Append(builder, inner, depth + 1);
        }
    }
}
