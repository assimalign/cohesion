using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs the header-phase compliance tests alone: a listener reports a failed establishment only through
/// the process-wide <c>Assimalign.Cohesion.Connections</c> event source.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AmqpHeaderPhaseCollection
{
    public const string Name = "AMQP header phase";
}
