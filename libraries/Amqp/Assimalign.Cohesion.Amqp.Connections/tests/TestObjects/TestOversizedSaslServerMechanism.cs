using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A server mechanism whose first step carries more data than a SASL frame holds: either a challenge or
/// the additional data of a successful outcome, so a test sees how the listener answers a mechanism that
/// breaks the 512-byte SASL frame limit.
/// </summary>
internal sealed class TestOversizedSaslServerMechanism : IAmqpSaslServerMechanism
{
    public const int DataSize = 600;

    private readonly bool _challenge;

    public TestOversizedSaslServerMechanism(bool challenge)
    {
        _challenge = challenge;
    }

    public AmqpSymbol Name => "X-OVERSIZED";

    public IAmqpSaslServerExchange CreateExchange(IConnection connection, string? hostName)
    {
        return new Exchange(_challenge);
    }

    private sealed class Exchange : IAmqpSaslServerExchange
    {
        private readonly bool _challenge;

        public Exchange(bool challenge)
        {
            _challenge = challenge;
        }

        public ValueTask<AmqpSaslServerStep> EvaluateAsync(ReadOnlyMemory<byte>? response, CancellationToken cancellationToken = default)
        {
            byte[] data = new byte[DataSize];

            return ValueTask.FromResult(_challenge
                ? AmqpSaslServerStep.Challenge(data)
                : AmqpSaslServerStep.Success("oversized-user", data));
        }
    }
}
