using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the transactional resource's side of a control link: it receives the controller's declares
/// and discharges and answers each with an outcome (AMQP 1.0 Part 4, sections 4.2 and 4.3).
/// </summary>
/// <remarks>
/// The transactions belong to the control link that declared them: a discharge names one the link declared,
/// and when the link ends, every transaction it has not discharged is rolled back (section 4.1).
/// </remarks>
public interface IAmqpTransactionCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Gets the control link the coordinator receives on: a receiving link whose target is an
    /// <see cref="AmqpCoordinator"/>.
    /// </summary>
    IAmqpReceiver Link { get; }

    /// <summary>
    /// Gets the identifiers of the transactions the control link declared and has not discharged.
    /// </summary>
    /// <returns>A snapshot, in the order the transactions were declared.</returns>
    IReadOnlyList<ReadOnlyMemory<byte>> GetActiveTransactions();

    /// <summary>
    /// Receives and answers control messages until the control link ends, then rolls back every
    /// transaction the link declared and did not discharge.
    /// </summary>
    /// <param name="cancellationToken">A token that stops the run: the coordinator closes the control link, rolls back its live transactions, and throws.</param>
    /// <returns>A task that completes when the control link has ended and its live transactions are rolled back.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the coordinator has already run, or when the handler declared an identifier longer than 32 bytes or one already live on the link.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the run is canceled.</exception>
    /// <remarks>
    /// An exception the <see cref="IAmqpTransactionHandler"/> throws other than an <see cref="AmqpException"/>
    /// closes the control link with <see cref="AmqpErrorCondition.InternalError"/>, rolls back the live
    /// transactions, and surfaces from the run. A rollback that throws surfaces after the others are tried,
    /// unless the run already failed.
    /// </remarks>
    Task RunAsync(CancellationToken cancellationToken = default);
}
