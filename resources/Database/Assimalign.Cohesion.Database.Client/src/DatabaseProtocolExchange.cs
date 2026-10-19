using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>A model-owned operation that one authenticated connection runs over its framed channel.</summary>
/// <typeparam name="TResult">The model's result type.</typeparam>
/// <remarks>
/// <para>
/// A model client derives from this type and implements
/// <see cref="ExecuteCoreAsync(ProtocolFrameReader, ProtocolFrameWriter, CancellationToken)"/>: it writes
/// the request and consumes the entire response before it returns. Only
/// <see cref="DatabaseConnection.ExecuteAsync{TResult}(DatabaseProtocolExchange{TResult}, CancellationToken)"/>
/// runs an exchange, so the entry point is internal to Database.Client and the constructor is protected:
/// Database.Client drives the type, and the model clients and the application implement it. The frame
/// reader and writer belong to the connection; an exchange never retains or disposes them.
/// </para>
/// <para>
/// The base owns the evidence the pool reads after a failure. Each run starts without it. An exchange that
/// consumed a complete, terminal response and then fails calls <see cref="MarkResponseComplete"/> before it
/// throws, and its connection stays reusable; a failure without that evidence discards the connection
/// whatever its error code. A successful return already proves a complete response. Transport and framing
/// failures always discard the connection. An exchange runs on one connection at a time.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseProtocolExchange<TResult>
{
    private bool _isResponseComplete;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseProtocolExchange{TResult}"/> class.
    /// </summary>
    /// <param name="family">The exact message family instance the operation requires.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="family"/> is null.</exception>
    /// <remarks>
    /// Protected, not private protected: the exchanges live in the Sql, KeyValuePair, Graph and Blob
    /// clients as well as here.
    /// </remarks>
    protected DatabaseProtocolExchange(ProtocolMessageFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        Family = family;
    }

    /// <summary>Gets the exact family instance required by this operation.</summary>
    /// <remarks>A connection bound to another instance rejects the exchange before it sends any bytes,
    /// even when that family reuses the same identifier bytes.</remarks>
    public ProtocolMessageFamily Family { get; }

    /// <summary>
    /// Gets whether the last run, having failed, consumed a terminal response and left the session ready
    /// for another exchange.
    /// </summary>
    internal bool IsResponseComplete => _isResponseComplete;

    /// <summary>Clears the completion evidence and runs the exchange.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The model-owned result.</returns>
    internal ValueTask<TResult> ExecuteAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
    {
        _isResponseComplete = false;
        return ExecuteCoreAsync(reader, writer, cancellationToken);
    }

    /// <summary>
    /// Records that the current run consumed a complete, terminal response, so a failure it throws next
    /// leaves the connection reusable.
    /// </summary>
    /// <remarks>
    /// Call it only after validating the whole response, for example a statement rejection the server sends
    /// as its entire response before returning to its ready loop. It has no effect on a successful return.
    /// </remarks>
    protected void MarkResponseComplete() => _isResponseComplete = true;

    /// <summary>Writes the request and consumes the model's response exchange.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The model-owned result.</returns>
    /// <exception cref="DatabaseClientException">The server rejects the operation or its response is invalid.</exception>
    /// <exception cref="ProtocolException">The model response is malformed; the connection maps it to a <see cref="DatabaseClientException"/> and discards the session.</exception>
    /// <exception cref="OperationCanceledException">The exchange is canceled.</exception>
    protected abstract ValueTask<TResult> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken);
}
