using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A header-phase compliance case: a scripted peer plays one side of establishment against a listener or
/// a factory, and the case states how establishment ends.
/// </summary>
/// <param name="Name">The case's name, unique in the corpus.</param>
/// <param name="Rule">The identifier of the rule the case exercises.</param>
/// <param name="Role">The engine under test: a listener, which the peer connects to, or a factory, which connects to the peer.</param>
/// <param name="Sasl">The SASL mechanism the engine is configured with.</param>
/// <param name="Script">The peer's steps, in order.</param>
/// <param name="Outcome">How establishment ends.</param>
/// <param name="Tls">The TLS the engine negotiates after the TLS protocol header, through <see cref="AmqpTestTlsLayer"/>.</param>
internal sealed record AmqpHeaderPhaseCase(
    string Name,
    string Rule,
    AmqpHeaderPhaseRole Role,
    AmqpHeaderPhaseSasl Sasl,
    IReadOnlyList<AmqpHeaderPhaseStep> Script,
    AmqpHeaderPhaseOutcome Outcome,
    AmqpHeaderPhaseTls Tls = AmqpHeaderPhaseTls.None)
{
    /// <summary>
    /// Gets whether the engine refuses the case: establishment fails, or the engine ends the script by closing the
    /// connection it opened.
    /// </summary>
    public bool Refuses => !Outcome.Opened || Script[^1].Kind == AmqpHeaderPhaseStepKind.ExpectClose;
}

/// <summary>
/// The engine a header-phase case drives.
/// </summary>
internal enum AmqpHeaderPhaseRole
{
    /// <summary>The engine is a listener; the scripted peer is the client.</summary>
    Listener,

    /// <summary>The engine is a factory; the scripted peer is the server.</summary>
    Factory
}

/// <summary>
/// The SASL mechanism a header-phase case configures the engine with.
/// </summary>
internal enum AmqpHeaderPhaseSasl
{
    /// <summary>The engine does not authenticate.</summary>
    None,

    /// <summary>The engine authenticates with <c>ANONYMOUS</c>.</summary>
    Anonymous,

    /// <summary>The engine authenticates with the test mechanism <c>X-CHALLENGE</c>, which runs one challenge.</summary>
    Challenge
}

/// <summary>
/// The TLS a header-phase case configures the engine with, through <see cref="AmqpTestTlsLayer"/>, whose stand-in
/// handshake a script plays as bytes.
/// </summary>
internal enum AmqpHeaderPhaseTls
{
    /// <summary>The engine has no TLS layer: a listener declines TLS, a factory does not ask for it.</summary>
    None,

    /// <summary>The engine has a TLS layer: a listener secures a peer that asks for TLS, a factory asks for it first.</summary>
    Layer,

    /// <summary>The engine is a listener with a TLS layer that requires TLS of every peer.</summary>
    Required
}
