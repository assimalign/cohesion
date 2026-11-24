using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A server mechanism whose exchange throws, standing in for application code that fails mid-exchange.
/// </summary>
internal sealed class TestThrowingSaslServerMechanism : IAmqpSaslServerMechanism
{
    public AmqpSymbol Name => "ANONYMOUS";

    public IAmqpSaslServerExchange CreateExchange(IConnection connection, string? hostName)
    {
        return new Exchange();
    }

    private sealed class Exchange : IAmqpSaslServerExchange
    {
        public ValueTask<AmqpSaslServerStep> EvaluateAsync(ReadOnlyMemory<byte>? response, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The credential store is unavailable.");
        }
    }
}
