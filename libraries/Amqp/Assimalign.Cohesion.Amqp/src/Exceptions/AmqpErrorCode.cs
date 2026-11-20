namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Categorizes the failures an <see cref="AmqpException"/> reports, so callers can branch on the kind of
/// failure without matching message text.
/// </summary>
/// <remarks>
/// Values are stable: a new code is additive, and renaming or renumbering one is a breaking change. The
/// AMQP condition behind a failure, when there is one, is in <see cref="AmqpException.Error"/>.
/// </remarks>
public enum AmqpErrorCode
{
    /// <summary>
    /// A failure that fits no other category.
    /// </summary>
    Other = 0,

    /// <summary>
    /// A peer violated the protocol: a frame or value could not be decoded, or a frame arrived that the
    /// connection, session or link state does not allow.
    /// </summary>
    ProtocolViolation = 1,

    /// <summary>
    /// The connection is closed: either peer closed it, or its carrier ended. The peer's error, if it sent
    /// one, is in <see cref="AmqpException.Error"/>.
    /// </summary>
    ConnectionClosed = 2,

    /// <summary>
    /// The session ended: either peer ended it, or its connection closed.
    /// </summary>
    SessionEnded = 3,

    /// <summary>
    /// The link detached: either peer detached it, its session ended, or the peer refused to attach it.
    /// </summary>
    LinkDetached = 4,

    /// <summary>
    /// SASL authentication failed, or the peers share no mechanism.
    /// </summary>
    AuthenticationFailed = 5,

    /// <summary>
    /// A message exceeds the largest message the link accepts.
    /// </summary>
    MessageSizeExceeded = 6,

    /// <summary>
    /// A transaction could not be declared or discharged, or the coordinator rolled it back.
    /// </summary>
    TransactionFailed = 7
}
