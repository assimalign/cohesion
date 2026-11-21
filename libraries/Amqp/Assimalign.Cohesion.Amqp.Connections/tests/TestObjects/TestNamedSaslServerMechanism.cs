using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A server mechanism with a chosen name that fails every exchange, so a test can size the
/// <c>sasl-mechanisms</c> frame a listener offers.
/// </summary>
internal sealed class TestNamedSaslServerMechanism : IAmqpSaslServerMechanism
{
    public TestNamedSaslServerMechanism(string name)
    {
        Name = name;
    }

    public AmqpSymbol Name { get; }

    public IAmqpSaslServerExchange CreateExchange(IConnection connection, string? hostName)
    {
        return new Exchange();
    }

    private sealed class Exchange : IAmqpSaslServerExchange
    {
        public ValueTask<AmqpSaslServerStep> EvaluateAsync(ReadOnlyMemory<byte>? response, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(AmqpSaslServerStep.Fail());
        }
    }
}
