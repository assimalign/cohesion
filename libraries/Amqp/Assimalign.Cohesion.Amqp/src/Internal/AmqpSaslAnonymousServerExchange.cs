using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Internal;

/// <summary>
/// The server exchange of <c>ANONYMOUS</c>: it accepts every client, whatever trace information it sent.
/// It holds no state, so every connection shares one instance.
/// </summary>
internal sealed class AmqpSaslAnonymousServerExchange : IAmqpSaslServerExchange
{
    private static readonly ValueTask<AmqpSaslServerStep> _accepted = ValueTask.FromResult(AmqpSaslServerStep.Success());

    private AmqpSaslAnonymousServerExchange()
    {
    }

    public static AmqpSaslAnonymousServerExchange Instance { get; } = new();

    public ValueTask<AmqpSaslServerStep> EvaluateAsync(ReadOnlyMemory<byte>? response, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return _accepted;
    }
}
