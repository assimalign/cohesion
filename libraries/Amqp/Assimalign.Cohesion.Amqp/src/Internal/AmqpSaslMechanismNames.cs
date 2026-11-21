namespace Assimalign.Cohesion.Amqp.Internal;

/// <summary>
/// The registered names of the SASL mechanisms the library ships.
/// </summary>
internal static class AmqpSaslMechanismNames
{
    /// <summary>
    /// Gets <c>PLAIN</c> (RFC 4616).
    /// </summary>
    public static AmqpSymbol Plain { get; } = new("PLAIN");

    /// <summary>
    /// Gets <c>ANONYMOUS</c> (RFC 4505).
    /// </summary>
    public static AmqpSymbol Anonymous { get; } = new("ANONYMOUS");

    /// <summary>
    /// Gets <c>EXTERNAL</c> (RFC 4422, appendix A).
    /// </summary>
    public static AmqpSymbol External { get; } = new("EXTERNAL");
}
