using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs the interoperability tests against AMQPNetLite.Core alone. The peer's frame trace is process-wide
/// static state, so one test at a time owns it, and the trace a failing test prints holds only that test's
/// frames.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AmqpInteropCollection
{
    public const string Name = "AMQP interop (AMQPNetLite.Core)";

    /// <summary>
    /// The trait every interoperability test carries: <c>--filter Category!=Interop</c> leaves only the
    /// engine's own tests, and <c>--filter Category=Interop</c> runs only these.
    /// </summary>
    public const string Category = "Interop";
}
