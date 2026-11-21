using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A server mechanism that answers the initial response with one challenge and accepts the client that
/// answers it with the challenge followed by <c>-answer</c>, so a test drives the challenge loop.
/// </summary>
internal sealed class TestChallengeSaslServerMechanism : IAmqpSaslServerMechanism
{
    public const string Challenge = "nonce";
    public const string Answer = "nonce-answer";
    public const string Identity = "challenged-user";
    public const string Welcome = "welcome";

    public AmqpSymbol Name => "X-CHALLENGE";

    public int ExchangeCount { get; private set; }

    public IAmqpSaslServerExchange CreateExchange(IConnection connection, string? hostName)
    {
        ExchangeCount++;
        return new Exchange();
    }

    private sealed class Exchange : IAmqpSaslServerExchange
    {
        private bool _challenged;

        public ValueTask<AmqpSaslServerStep> EvaluateAsync(ReadOnlyMemory<byte>? response, CancellationToken cancellationToken = default)
        {
            if (!_challenged)
            {
                _challenged = true;
                return ValueTask.FromResult(AmqpSaslServerStep.Challenge(Encoding.ASCII.GetBytes(Challenge)));
            }

            bool answered = response is ReadOnlyMemory<byte> bytes && Encoding.ASCII.GetString(bytes.Span) == Answer;

            return ValueTask.FromResult(answered
                ? AmqpSaslServerStep.Success(Identity, Encoding.ASCII.GetBytes(Welcome))
                : AmqpSaslServerStep.Fail());
        }
    }
}
