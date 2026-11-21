namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A compliance map in these tests: a list of AMQP 1.0 rules, or for the type system its types, each with the
/// tests or corpus cases that pin it.
/// </summary>
internal enum AmqpComplianceMap
{
    /// <summary>
    /// No map holds the rule.
    /// </summary>
    None,

    /// <summary>
    /// The header-phase rules <c>H1</c> to <c>H20</c> in <see cref="AmqpHeaderPhaseCorpus"/>.
    /// </summary>
    HeaderPhase,

    /// <summary>
    /// The Part 1 types in <see cref="AmqpTypeSystemCorpus"/>, named by type rather than by rule.
    /// </summary>
    TypeSystem,

    /// <summary>
    /// The message-format rules <c>MF1</c> to <c>MF9</c> in <see cref="AmqpMessageFormatCorpus"/>.
    /// </summary>
    MessageFormat,

    /// <summary>
    /// The terminus rules <c>T1</c> to <c>T10</c> in <see cref="AmqpTerminusCorpus"/>.
    /// </summary>
    Terminus,

    /// <summary>
    /// The transfer rules <c>F1</c> to <c>F20</c> in <see cref="AmqpTransferComplianceTests"/>.
    /// </summary>
    Transfer,

    /// <summary>
    /// The transaction rules <c>T1</c> to <c>T14</c> in <see cref="AmqpTransactionComplianceTests"/>.
    /// </summary>
    Transaction,

    /// <summary>
    /// The resume rules <c>R1</c> to <c>R11</c> in <see cref="AmqpResumeComplianceTests"/>.
    /// </summary>
    Resume,

    /// <summary>
    /// The performative rules <c>P1</c> to <c>P8</c> in <see cref="AmqpPerformativeTests"/>: how a composite
    /// type is encoded, and the definitions every performative's fields hold to.
    /// </summary>
    Performative,

    /// <summary>
    /// The connection rules <c>C1</c> to <c>C8</c> in <see cref="AmqpConnectionComplianceTests"/>.
    /// </summary>
    Connection,

    /// <summary>
    /// The session rules <c>S1</c> to <c>S11</c> in <see cref="AmqpSessionComplianceTests"/>.
    /// </summary>
    Session,

    /// <summary>
    /// The link rules <c>L1</c> to <c>L11</c> in <see cref="AmqpLinkComplianceTests"/>.
    /// </summary>
    Link,

    /// <summary>
    /// The settlement rules <c>DS1</c> to <c>DS6</c> in <see cref="AmqpSettlementComplianceTests"/>.
    /// </summary>
    Settlement
}
