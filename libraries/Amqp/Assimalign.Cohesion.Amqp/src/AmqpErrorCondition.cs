namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Provides the error conditions AMQP 1.0 defines (Part 2, sections 2.8.15 to 2.8.18, and Part 4,
/// section 4.5.8).
/// </summary>
/// <remarks>
/// A peer may send a condition this class does not list: <see cref="AmqpError.Condition"/> accepts any
/// symbol. Conditions compare ordinally, so compare a received condition with these values using
/// <c>==</c>.
/// </remarks>
public static class AmqpErrorCondition
{
    /// <summary>
    /// Gets <c>amqp:internal-error</c>: an internal error occurred.
    /// </summary>
    public static AmqpSymbol InternalError { get; } = new("amqp:internal-error");

    /// <summary>
    /// Gets <c>amqp:not-found</c>: a peer attempted to work with a remote entity that does not exist.
    /// </summary>
    public static AmqpSymbol NotFound { get; } = new("amqp:not-found");

    /// <summary>
    /// Gets <c>amqp:unauthorized-access</c>: a peer attempted to work with a remote entity it has no access to.
    /// </summary>
    public static AmqpSymbol UnauthorizedAccess { get; } = new("amqp:unauthorized-access");

    /// <summary>
    /// Gets <c>amqp:decode-error</c>: data could not be decoded.
    /// </summary>
    public static AmqpSymbol DecodeError { get; } = new("amqp:decode-error");

    /// <summary>
    /// Gets <c>amqp:resource-limit-exceeded</c>: a peer exceeded its resource allocation.
    /// </summary>
    public static AmqpSymbol ResourceLimitExceeded { get; } = new("amqp:resource-limit-exceeded");

    /// <summary>
    /// Gets <c>amqp:not-allowed</c>: a peer tried to use a frame in a manner that is inconsistent with the specification.
    /// </summary>
    public static AmqpSymbol NotAllowed { get; } = new("amqp:not-allowed");

    /// <summary>
    /// Gets <c>amqp:invalid-field</c>: an invalid field was passed in a frame body.
    /// </summary>
    public static AmqpSymbol InvalidField { get; } = new("amqp:invalid-field");

    /// <summary>
    /// Gets <c>amqp:not-implemented</c>: a peer tried to use functionality that is not implemented.
    /// </summary>
    public static AmqpSymbol NotImplemented { get; } = new("amqp:not-implemented");

    /// <summary>
    /// Gets <c>amqp:resource-locked</c>: a peer tried to access a resource that is locked by another.
    /// </summary>
    public static AmqpSymbol ResourceLocked { get; } = new("amqp:resource-locked");

    /// <summary>
    /// Gets <c>amqp:precondition-failed</c>: a peer made a request that a precondition prevents.
    /// </summary>
    public static AmqpSymbol PreconditionFailed { get; } = new("amqp:precondition-failed");

    /// <summary>
    /// Gets <c>amqp:resource-deleted</c>: a remote entity the peer worked with has been deleted.
    /// </summary>
    public static AmqpSymbol ResourceDeleted { get; } = new("amqp:resource-deleted");

    /// <summary>
    /// Gets <c>amqp:illegal-state</c>: a peer sent a frame that is not permitted in the current state.
    /// </summary>
    public static AmqpSymbol IllegalState { get; } = new("amqp:illegal-state");

    /// <summary>
    /// Gets <c>amqp:frame-size-too-small</c>: a peer cannot send a frame because its negotiated maximum
    /// frame size is too small for it.
    /// </summary>
    public static AmqpSymbol FrameSizeTooSmall { get; } = new("amqp:frame-size-too-small");

    /// <summary>
    /// Gets <c>amqp:connection:forced</c>: an operator intervened to close the connection.
    /// </summary>
    public static AmqpSymbol ConnectionForced { get; } = new("amqp:connection:forced");

    /// <summary>
    /// Gets <c>amqp:connection:framing-error</c>: a valid frame header could not be formed from the incoming bytes.
    /// </summary>
    public static AmqpSymbol FramingError { get; } = new("amqp:connection:framing-error");

    /// <summary>
    /// Gets <c>amqp:connection:redirect</c>: the container is no longer available on this connection, and
    /// the error's information names where to reconnect.
    /// </summary>
    public static AmqpSymbol ConnectionRedirect { get; } = new("amqp:connection:redirect");

    /// <summary>
    /// Gets <c>amqp:session:window-violation</c>: a peer sent more transfer frames than the session's
    /// incoming window allowed.
    /// </summary>
    public static AmqpSymbol WindowViolation { get; } = new("amqp:session:window-violation");

    /// <summary>
    /// Gets <c>amqp:session:errant-link</c>: input was received for a link that was detached with an error.
    /// </summary>
    public static AmqpSymbol ErrantLink { get; } = new("amqp:session:errant-link");

    /// <summary>
    /// Gets <c>amqp:session:handle-in-use</c>: an attach named a handle that is already in use.
    /// </summary>
    public static AmqpSymbol HandleInUse { get; } = new("amqp:session:handle-in-use");

    /// <summary>
    /// Gets <c>amqp:session:unattached-handle</c>: a frame other than attach named a handle that is not attached.
    /// </summary>
    public static AmqpSymbol UnattachedHandle { get; } = new("amqp:session:unattached-handle");

    /// <summary>
    /// Gets <c>amqp:link:detach-forced</c>: an operator intervened to detach the link.
    /// </summary>
    public static AmqpSymbol DetachForced { get; } = new("amqp:link:detach-forced");

    /// <summary>
    /// Gets <c>amqp:link:transfer-limit-exceeded</c>: the peer sent a transfer without link credit.
    /// </summary>
    public static AmqpSymbol TransferLimitExceeded { get; } = new("amqp:link:transfer-limit-exceeded");

    /// <summary>
    /// Gets <c>amqp:link:message-size-exceeded</c>: a message exceeded the link's maximum message size.
    /// </summary>
    public static AmqpSymbol MessageSizeExceeded { get; } = new("amqp:link:message-size-exceeded");

    /// <summary>
    /// Gets <c>amqp:link:redirect</c>: the address is no longer serviced on this link, and the error's
    /// information names where to attach instead.
    /// </summary>
    public static AmqpSymbol LinkRedirect { get; } = new("amqp:link:redirect");

    /// <summary>
    /// Gets <c>amqp:link:stolen</c>: another link with the same name took over the link.
    /// </summary>
    public static AmqpSymbol Stolen { get; } = new("amqp:link:stolen");

    /// <summary>
    /// Gets <c>amqp:transaction:unknown-id</c>: the transaction identifier is not known to the coordinator.
    /// </summary>
    public static AmqpSymbol TransactionUnknownId { get; } = new("amqp:transaction:unknown-id");

    /// <summary>
    /// Gets <c>amqp:transaction:rollback</c>: the coordinator rolled the transaction back.
    /// </summary>
    public static AmqpSymbol TransactionRollback { get; } = new("amqp:transaction:rollback");

    /// <summary>
    /// Gets <c>amqp:transaction:timeout</c>: the transaction timed out.
    /// </summary>
    public static AmqpSymbol TransactionTimeout { get; } = new("amqp:transaction:timeout");
}
