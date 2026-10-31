using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client.Internal;
using Assimalign.Cohesion.Database.Sql.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client;

public sealed partial class SqlConnection
{
    /// <summary>Streams a snapshot-consistent table export into a caller-owned destination.</summary>
    /// <param name="statement">A COPY table TO STDOUT statement selecting text or CSV options.</param>
    /// <param name="destination">The writable destination; it remains open on every outcome.</param>
    /// <param name="cancellationToken">Cancellation token for the complete exchange and destination writes.</param>
    /// <returns>The verified number of exported data rows, excluding an optional header.</returns>
    /// <exception cref="ArgumentException">The statement is blank or the destination is not writable.</exception>
    /// <exception cref="ArgumentNullException">The destination is null.</exception>
    /// <exception cref="ObjectDisposedException">This connection is disposed or aborted.</exception>
    /// <exception cref="InvalidOperationException">Another command is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled; its connection is discarded.</exception>
    /// <exception cref="SqlClientException">The capability is absent or the server or protocol rejects the export.</exception>
    /// <exception cref="IOException">Destination delivery fails; its connection is discarded.</exception>
    /// <remarks>Retains bounded buffers. Failure may leave a partial export in the destination.
    /// Coded statement errors before streaming preserve the connection and transaction. Late, canceled or
    /// malformed transfers discard the connection; missing capability refuses before sending.
    /// The destination must honor cancellation and must not reenter this connection from its write callbacks.</remarks>
    public async ValueTask<long> CopyToAsync(string statement, Stream destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) { throw new ArgumentException(ErrorMessages.CopyDestinationInvalid, nameof(destination)); }
        cancellationToken.ThrowIfCancellationRequested();
        NotifyExecuting(statement, 0);
        long start = Stopwatch.GetTimestamp();
        bool startWritten = SqlClientEventSource.Log.CommandStart(this, 0);
        Exception? failure = null;
        SqlClientException? reported = null;
        long rows = -1;
        SqlCopyExchange? exchange = null;
        try
        {
            if (!_connection.Capabilities.Contains(SqlProtocol.OutboundStream.Name))
            {
                throw new DatabaseClientException(ProtocolErrorCode.ExecutionFailure, ErrorMessages.CopyCapabilityRequired,
                    [new ProtocolDiagnostic(ProtocolDiagnosticSeverity.Error, "CS05094", ErrorMessages.CopyCapabilityRequired, null, null, 1)], null);
            }
            exchange = new SqlCopyExchange(statement, destination);
            rows = await _connection.ExecuteAsync(exchange, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            reported = _connection.IsOpen && exchange is not { Started: true } ? SqlClientException.FromClientException(exception) : SqlClientException.FromStreamingException(exception);
            failure = reported;
        }
        catch (SqlCopyDeliveryException exception) { failure = exception.Failure; exception.Rethrow(); throw; }
        catch (Exception exception) when (SqlClientEventSource.CaptureFailure(exception, out failure)) { throw; }
        finally
        {
            try
            {
                if (exchange is { Entered: true } && (!exchange.Terminal || !_connection.IsOpen))
                {
                    // Execute has released admission before abort joins transport teardown.
                    // Preserve the original delivery/protocol failure if teardown fails too.
                    try { await _connection.AbortAsync().ConfigureAwait(false); }
                    catch (Exception exception) when (failure is not null && exception is not OutOfMemoryException) { }
                }
            }
            finally { SqlClientEventSource.Log.CommandEnded(this, startWritten, failure, rows, -1, start); }
        }
        if (reported is not null)
        {
            NotifyFailed(statement, reported, Stopwatch.GetElapsedTime(start));
            throw reported;
        }
        NotifyExecuted(statement, rows, -1, Stopwatch.GetElapsedTime(start));
        return rows;
    }
}
