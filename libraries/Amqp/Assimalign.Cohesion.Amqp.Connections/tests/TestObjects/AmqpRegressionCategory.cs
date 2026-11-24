namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The kind of input or event that exposed a defect in the regression register (#433).
/// </summary>
internal enum AmqpRegressionCategory
{
    /// <summary>
    /// A frame that was too large, written only in part, or read under the wrong limit.
    /// </summary>
    MalformedFrame,

    /// <summary>
    /// A message or a value that the type system or the message format refuses, or that changed on its way
    /// through the codec.
    /// </summary>
    MalformedMessage,

    /// <summary>
    /// An operation on a connection or link in a state where it behaved wrongly, such as a failed answer or a
    /// disposal.
    /// </summary>
    InvalidState,

    /// <summary>
    /// A protocol header, SASL exchange or <c>open</c> that the engine negotiated wrongly or stalled on.
    /// </summary>
    InvalidNegotiation
}
