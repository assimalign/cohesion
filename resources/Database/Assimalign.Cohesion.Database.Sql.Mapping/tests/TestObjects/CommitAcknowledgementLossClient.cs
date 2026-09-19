using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

// Faults precisely where the mapper receives a COMMIT result. Both possible server outcomes
// present the identical transport error; the forwarded branch commits through the real engine.
internal sealed class CommitAcknowledgementLossClient(ISqlClient inner, bool commitReachesServer) : ISqlClient
{
    internal int AbortCount { get; private set; }

    public DatabaseConnectionSettings Settings => inner.Settings;

    public async ValueTask<ISqlConnection> ConnectAsync(CancellationToken cancellationToken = default)
        => new FaultingConnection(await inner.ConnectAsync(cancellationToken), this, commitReachesServer);

    // The harness owns the wrapped client.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FaultingConnection(ISqlConnection connection, CommitAcknowledgementLossClient owner,
        bool reachesServer) : ISqlConnection
    {
        public string Database => connection.Database;
        public bool IsOpen => connection.IsOpen;

        public ValueTask<SqlResultSet> QueryAsync(SqlCommand command, CancellationToken cancellationToken = default)
            => connection.QueryAsync(command, cancellationToken);

        public ValueTask<SqlResultSet> QueryAsync(string commandText,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
            => connection.QueryAsync(commandText, parameters, cancellationToken);

        public async ValueTask<long> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default)
        {
            bool commit = IsCommit(command.CommandText);
            if (commit && !reachesServer)
            {
                throw ConnectionLost();
            }
            long affected = await connection.ExecuteAsync(command, cancellationToken);
            if (commit)
            {
                throw ConnectionLost();
            }
            return affected;
        }

        public async ValueTask<long> ExecuteAsync(string commandText,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
        {
            bool commit = IsCommit(commandText);
            if (commit && !reachesServer)
            {
                throw ConnectionLost();
            }
            long affected = await connection.ExecuteAsync(commandText, parameters, cancellationToken);
            if (commit)
            {
                throw ConnectionLost();
            }
            return affected;
        }

        public ValueTask<T?> ExecuteScalarAsync<T>(SqlCommand command, CancellationToken cancellationToken = default)
            => connection.ExecuteScalarAsync<T>(command, cancellationToken);

        public ValueTask DisposeAsync() => connection.DisposeAsync();

        public ValueTask AbortAsync()
        {
            owner.AbortCount++;
            return connection.AbortAsync();
        }

        private static bool IsCommit(string text)
            => text.Trim().TrimEnd(';').Equals("COMMIT", StringComparison.OrdinalIgnoreCase);

        private static SqlClientException ConnectionLost()
            => new(SqlClientErrorKind.Unavailable, ProtocolErrorCode.Unavailable,
                "Injected loss of the COMMIT acknowledgement at the SQL client boundary.");
    }
}
