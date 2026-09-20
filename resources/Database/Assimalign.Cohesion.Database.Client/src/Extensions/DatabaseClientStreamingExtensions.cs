using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>Executes streaming model responses using leases owned by the returned streams.</summary>
public static partial class DatabaseClientStreamingExtensions
{
    extension(IDatabaseClient client)
    {
        /// <summary>Rents a connection and starts a bounded, nonseekable response stream.</summary>
        /// <param name="exchange">The model operation, bound to the client's exact message family.</param>
        /// <param name="cancellationToken">Cancellation for the rental and the returned stream's lifetime.</param>
        /// <returns>A readable stream after the operation's initial metadata has been validated.</returns>
        /// <remarks>
        /// The caller must dispose the returned stream. Its lease remains reserved through successful
        /// EOF until disposal. Failure, cancellation, or disposal before verified exchange completion
        /// closes the connection and releases its lease. Both synchronous and asynchronous reads are
        /// supported; canceling a read token cancels and joins the entire exchange. A failed response
        /// never becomes successful EOF, and subsequent reads retain its original failure.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The client or exchange is null.</exception>
        /// <exception cref="ArgumentException">The exchange belongs to a different message family.</exception>
        /// <exception cref="DatabaseClientException">The server rejects the operation or the connection fails.</exception>
        /// <exception cref="OperationCanceledException">The rental or streaming operation is canceled.</exception>
        public async ValueTask<Stream> ExecuteStreamingAsync(IDatabaseStreamingExchange exchange,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(exchange);
            IDatabaseConnection connection = await client.RentAsync(cancellationToken).ConfigureAwait(false);
            return await DatabaseDownloadStream.CreateAsync(connection, exchange, cancellationToken,
                ownsConnection: true).ConfigureAwait(false);
        }
    }
}
