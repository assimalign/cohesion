using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>A model-owned operation on an authenticated framed connection.</summary>
/// <typeparam name="TResult">The model's result type.</typeparam>
/// <remarks>The operation must consume its entire exchange before returning. The frame
/// reader and writer belong to the connection and must not be retained or disposed.</remarks>
public interface IDatabaseProtocolExchange<TResult>
{
    /// <summary>Gets the exact family instance required by this operation.</summary>
    ProtocolMessageFamily Family { get; }

    /// <summary>Gets whether a failed invocation consumed a terminal response and left the session ready for another exchange.</summary>
    /// <remarks>
    /// The default is false: an unverified failure must discard the connection regardless of its error code.
    /// Implementations reset this state before each invocation and set it only after validating a complete,
    /// reusable response. Successful return already guarantees completion. Transport and framing failures
    /// always invalidate the connection, even if this property is true.
    /// </remarks>
    bool IsResponseComplete => false;

    /// <summary>Writes the request and consumes the model's response exchange.</summary>
    /// <param name="reader">The connection's family-validating frame reader.</param>
    /// <param name="writer">The connection's family-validating frame writer.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The model-owned result.</returns>
    /// <exception cref="DatabaseClientException">The server rejects the operation or its response is invalid.</exception>
    ValueTask<TResult> ExecuteAsync(IProtocolFrameReader reader, IProtocolFrameWriter writer, CancellationToken cancellationToken = default);
}
