using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

/// <summary>
/// Pins every symbol the library names against the specification's spelling: a peer matches these on the
/// wire character for character.
/// </summary>
public class AmqpWellKnownSymbolTests
{
    public static TheoryData<AmqpSymbol, string> Symbols => new()
    {
        // AMQP 1.0 Part 2, section 2.8.15 (amqp-error).
        { AmqpErrorCondition.InternalError, "amqp:internal-error" },
        { AmqpErrorCondition.NotFound, "amqp:not-found" },
        { AmqpErrorCondition.UnauthorizedAccess, "amqp:unauthorized-access" },
        { AmqpErrorCondition.DecodeError, "amqp:decode-error" },
        { AmqpErrorCondition.ResourceLimitExceeded, "amqp:resource-limit-exceeded" },
        { AmqpErrorCondition.NotAllowed, "amqp:not-allowed" },
        { AmqpErrorCondition.InvalidField, "amqp:invalid-field" },
        { AmqpErrorCondition.NotImplemented, "amqp:not-implemented" },
        { AmqpErrorCondition.ResourceLocked, "amqp:resource-locked" },
        { AmqpErrorCondition.PreconditionFailed, "amqp:precondition-failed" },
        { AmqpErrorCondition.ResourceDeleted, "amqp:resource-deleted" },
        { AmqpErrorCondition.IllegalState, "amqp:illegal-state" },
        { AmqpErrorCondition.FrameSizeTooSmall, "amqp:frame-size-too-small" },

        // Part 2, section 2.8.16 (connection-error).
        { AmqpErrorCondition.ConnectionForced, "amqp:connection:forced" },
        { AmqpErrorCondition.FramingError, "amqp:connection:framing-error" },
        { AmqpErrorCondition.ConnectionRedirect, "amqp:connection:redirect" },

        // Part 2, section 2.8.17 (session-error).
        { AmqpErrorCondition.WindowViolation, "amqp:session:window-violation" },
        { AmqpErrorCondition.ErrantLink, "amqp:session:errant-link" },
        { AmqpErrorCondition.HandleInUse, "amqp:session:handle-in-use" },
        { AmqpErrorCondition.UnattachedHandle, "amqp:session:unattached-handle" },

        // Part 2, section 2.8.18 (link-error).
        { AmqpErrorCondition.DetachForced, "amqp:link:detach-forced" },
        { AmqpErrorCondition.TransferLimitExceeded, "amqp:link:transfer-limit-exceeded" },
        { AmqpErrorCondition.MessageSizeExceeded, "amqp:link:message-size-exceeded" },
        { AmqpErrorCondition.LinkRedirect, "amqp:link:redirect" },
        { AmqpErrorCondition.Stolen, "amqp:link:stolen" },

        // Part 4, section 4.5.8 (transaction-error).
        { AmqpErrorCondition.TransactionUnknownId, "amqp:transaction:unknown-id" },
        { AmqpErrorCondition.TransactionRollback, "amqp:transaction:rollback" },
        { AmqpErrorCondition.TransactionTimeout, "amqp:transaction:timeout" },

        // Part 4, section 4.5.7 (txn-capability).
        { AmqpTransactionCapability.LocalTransactions, "amqp:local-transactions" },
        { AmqpTransactionCapability.DistributedTransactions, "amqp:distributed-transactions" },
        { AmqpTransactionCapability.PromotableTransactions, "amqp:promotable-transactions" },
        { AmqpTransactionCapability.MultipleTransactionsPerSession, "amqp:multi-txns-per-ssn" },
        { AmqpTransactionCapability.MultipleSessionsPerTransaction, "amqp:multi-ssns-per-txn" },

        // Part 3, section 3.5.6 (terminus-expiry-policy).
        { AmqpTerminusExpiryPolicy.LinkDetach, "link-detach" },
        { AmqpTerminusExpiryPolicy.SessionEnd, "session-end" },
        { AmqpTerminusExpiryPolicy.ConnectionClose, "connection-close" },
        { AmqpTerminusExpiryPolicy.Never, "never" },

        // Part 3, section 3.5.7 (std-dist-mode).
        { AmqpDistributionMode.Move, "move" },
        { AmqpDistributionMode.Copy, "copy" }
    };

    [Theory(DisplayName = "Cohesion Test [Amqp] - Value: Should spell each well-known symbol as the specification does")]
    [MemberData(nameof(Symbols))]
    public void Value_OnWellKnownSymbol_ShouldMatchSpecification(AmqpSymbol symbol, string expected)
    {
        // Act
        string value = symbol.Value;

        // Assert
        value.ShouldBe(expected);
    }
}
