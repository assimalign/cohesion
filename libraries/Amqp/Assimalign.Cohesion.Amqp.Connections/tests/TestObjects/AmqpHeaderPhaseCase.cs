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
internal sealed record AmqpHeaderPhaseCase(
    string Name,
    string Rule,
    AmqpHeaderPhaseRole Role,
    AmqpHeaderPhaseSasl Sasl,
    IReadOnlyList<AmqpHeaderPhaseStep> Script,
    AmqpHeaderPhaseOutcome Outcome)
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
