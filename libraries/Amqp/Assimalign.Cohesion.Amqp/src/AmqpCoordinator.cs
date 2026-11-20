using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the transaction coordinator target: the target of a control link that declares and
/// discharges transactions (AMQP 1.0 Part 4, section 4.5.1).
/// </summary>
/// <remarks>
/// A transactional client attaches a sending link whose target is a coordinator, then sends
/// <see cref="AmqpDeclare"/> and <see cref="AmqpDischarge"/> messages on it; <see cref="AmqpTransactionController"/>
/// does both. A transaction resource accepts such a link from <see cref="IAmqpSession.AcceptLinkAsync(System.Threading.CancellationToken)"/>
/// when its <see cref="IAmqpLinkRequest.Target"/> is a coordinator.
/// </remarks>
public sealed class AmqpCoordinator : IAmqpTarget
{
    /// <summary>
    /// Initializes a new coordinator target.
    /// </summary>
    public AmqpCoordinator()
    {
    }

    /// <summary>
    /// Gets or sets the transaction capabilities the endpoint supports or requires, such as
    /// <see cref="AmqpTransactionCapability.LocalTransactions"/>, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Capabilities { get; set; }
}
