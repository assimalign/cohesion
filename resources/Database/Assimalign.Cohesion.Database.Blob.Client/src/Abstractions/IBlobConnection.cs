using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>A leased connection bound to one Blob database, allowing one exchange at a time.</summary>
/// <remarks>
/// A download or listing holds the exchange until completion or disposal. Dispose returned streams
/// and enumerators before starting another operation. Disposing this connection cancels and waits
/// for its active exchange before returning the pool lease. Failed exchanges close the connection.
/// </remarks>
public interface IBlobConnection : IAsyncDisposable
{
    /// <summary>Gets the database selected at handshake.</summary>
    string Database { get; }

    /// <summary>Gets whether the connection remains open and usable.</summary>
    bool IsOpen { get; }

    /// <summary>Streams content and waits for the server's atomic publication acknowledgement.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The ordinal, case-sensitive blob name.</param>
    /// <param name="source">A readable stream, left open and read from its current position.</param>
    /// <param name="contentType">The declared media type, or an empty string when unspecified.</param>
    /// <param name="length">The exact remaining byte count, or -1 for an unknown length.</param>
    /// <param name="overwrite">Whether an existing blob may be replaced.</param>
    /// <param name="cancellationToken">Cancellation token for source reads and the entire wire exchange.</param>
    /// <returns>The committed content length.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source is unreadable or the length is invalid.</exception>
    /// <exception cref="BlobClientException">The server rejects the write or the transfer fails.</exception>
    /// <exception cref="OperationCanceledException">The transfer is canceled; its connection is closed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    ValueTask<long> UploadAsync(string container, string name, Stream source,
        string contentType = "application/octet-stream", long length = -1, bool overwrite = true,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a download and returns a bounded, nonseekable readable stream.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The ordinal, case-sensitive blob name.</param>
    /// <param name="cancellationToken">Cancellation token that remains active for the returned stream's lifetime.</param>
    /// <returns>A stream after its transfer metadata has been validated. The caller must dispose it.</returns>
    /// <exception cref="BlobClientException">The server rejects the read or the transfer fails before its metadata arrives.</exception>
    /// <exception cref="OperationCanceledException">The download is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    /// <remarks>
    /// Later server errors, malformed completion, and truncation throw <see cref="BlobClientException"/>
    /// from stream reads; they never produce successful EOF. Both synchronous and asynchronous reads
    /// are supported. Canceling a ReadAsync token cancels the entire transfer. Early stream disposal
    /// aborts the exchange and closes the connection; fully verified downloads allow connection reuse.
    /// </remarks>
    ValueTask<Stream> DownloadAsync(string container, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a blob.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>True if a blob was deleted; false if it did not exist.</returns>
    /// <exception cref="BlobClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    ValueTask<bool> DeleteAsync(string container, string name, CancellationToken cancellationToken = default);

    /// <summary>Reads a blob's catalog metadata.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The properties, or null if the blob does not exist.</returns>
    /// <exception cref="BlobClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    ValueTask<BlobProperties?> GetPropertiesAsync(string container, string name, CancellationToken cancellationToken = default);

    /// <summary>Enumerates blob metadata matching an ordinal prefix using bounded client buffering.</summary>
    /// <param name="container">The container within this connection's database.</param>
    /// <param name="prefix">An ordinal prefix, or null to list all blobs.</param>
    /// <param name="cancellationToken">Cancellation token for enumeration and the exchange.</param>
    /// <returns>The matching metadata. Enumeration owns the exchange until completed or disposed.</returns>
    /// <exception cref="BlobClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The connection is disposed or unusable.</exception>
    IAsyncEnumerable<BlobProperties> GetBlobsAsync(string container, string? prefix = null,
        CancellationToken cancellationToken = default);
}
