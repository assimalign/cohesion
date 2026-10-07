using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client.Internal;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>Executes SQL exchanges using a shared connection bound to the SQL family.</summary>
/// <remarks>
/// It stays an extension because <see cref="DatabaseConnection"/> lives in Database.Client, which
/// references no model package.
/// </remarks>
public static partial class SqlProtocolConnectionExtensions
{
    extension(DatabaseConnection connection)
    {
        /// <summary>Encodes SQL parameters and materializes the complete SQL response.</summary>
        /// <param name="statement">The SQL statement text.</param>
        /// <param name="parameters">Values keyed by bare parameter name.</param>
        /// <param name="cancellationToken">Cancellation token for the exchange.</param>
        /// <returns>The materialized SQL result.</returns>
        /// <exception cref="ArgumentNullException">The connection is null.</exception>
        /// <exception cref="ArgumentException">The statement is empty or the connection belongs to another family.</exception>
        /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
        /// <exception cref="ObjectDisposedException">The rental has been returned and the pool has not rented this instance again.</exception>
        /// <exception cref="OperationCanceledException">The exchange is canceled, which marks the connection broken.</exception>
        /// <exception cref="DatabaseClientException">The server rejects the statement or the connection fails.</exception>
        public ValueTask<DatabaseClientResult> ExecuteAsync(
            string statement,
            IReadOnlyDictionary<string, object?>? parameters = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            return connection.ExecuteAsync(new SqlExecuteExchange(statement, parameters), cancellationToken);
        }
    }
}
