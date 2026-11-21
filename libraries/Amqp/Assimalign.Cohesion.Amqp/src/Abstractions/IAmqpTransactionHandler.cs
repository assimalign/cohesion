using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Performs the declares and discharges a transaction coordinator receives: the transactional resource's
/// side of AMQP 1.0 Part 4, such as a broker's transaction registry.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="AmqpTransactionCoordinator"/> calls the handler one control message at a time, in the
/// order the controller sent them. To refuse a declare or discharge, throw an <see cref="AmqpException"/>
/// whose <see cref="AmqpException.Error"/> carries the transaction error, such as
/// <see cref="AmqpErrorCondition.TransactionRollback"/> for a commit the resource rolled back instead; the
/// coordinator conveys it to the controller (sections 4.2 and 4.3).
/// </para>
/// <para>
/// Two rules end the control link rather than refuse a discharge: a transaction against which a delivery is
/// partly posted (section 4.4.1, see <see cref="IAmqpReceiver.PartialDeliveryState"/>), and an outcome
/// associated with another live transaction than its delivery (section 4.4.2). For those, roll the
/// transaction back, close <see cref="IAmqpTransactionCoordinator.Link"/> with
/// <see cref="AmqpErrorCondition.TransactionRollback"/>, and return; the coordinator then rolls back the
/// link's other transactions.
/// </para>
/// </remarks>
public interface IAmqpTransactionHandler
{
    /// <summary>
    /// Declares a transaction and allocates its identifier.
    /// </summary>
    /// <param name="declare">The controller's declare. A <see cref="AmqpDeclare.GlobalId"/> asks for a distributed transaction.</param>
    /// <param name="cancellationToken">A token the coordinator's run passes on.</param>
    /// <returns>
    /// The identifier, at most 32 bytes (section 4.5.4) and unique among the transactions the control link
    /// has not discharged. Work on any link in the controlling session, or on the connection when the
    /// coordinator supports <see cref="AmqpTransactionCapability.MultipleSessionsPerTransaction"/>, names it.
    /// </returns>
    /// <exception cref="AmqpException">Thrown to refuse the declare; <see cref="AmqpException.Error"/> is the error the controller receives.</exception>
    ValueTask<ReadOnlyMemory<byte>> DeclareAsync(AmqpDeclare declare, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discharges a transaction the control link declared: commits its work, or rolls it back.
    /// </summary>
    /// <param name="transactionId">The identifier <see cref="DeclareAsync(AmqpDeclare, CancellationToken)"/> allocated.</param>
    /// <param name="fail">
    /// <see langword="true"/> to roll the transaction back, which must always succeed (section 4.3);
    /// <see langword="false"/> to commit it. The coordinator also rolls back each transaction still live when
    /// the control link ends (section 4.1).
    /// </param>
    /// <param name="cancellationToken">A token the coordinator's run passes on; a rollback when the control link ends receives none.</param>
    /// <returns>A task that completes when the transaction is discharged.</returns>
    /// <exception cref="AmqpException">Thrown to refuse the discharge; <see cref="AmqpException.Error"/> is the error the controller receives. The transaction counts as discharged either way.</exception>
    ValueTask DischargeAsync(ReadOnlyMemory<byte> transactionId, bool fail, CancellationToken cancellationToken = default);
}
