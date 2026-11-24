using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The client half of <see cref="TestChallengeSaslServerMechanism"/>: it sends no initial response and
/// answers each challenge with the challenge followed by <c>-answer</c>, or with <see cref="WrongAnswer"/>.
/// </summary>
internal sealed class TestChallengeSaslClientMechanism : IAmqpSaslClientMechanism
{
    public AmqpSymbol Name => "X-CHALLENGE";

    public bool WrongAnswer { get; init; }

    public string? CompletedWith { get; private set; }

    public IAmqpSaslClientExchange CreateExchange(IConnection connection, string? hostName)
    {
        return new Exchange(this);
    }

    private sealed class Exchange : IAmqpSaslClientExchange
    {
        private readonly TestChallengeSaslClientMechanism _mechanism;

        public Exchange(TestChallengeSaslClientMechanism mechanism)
        {
            _mechanism = mechanism;
        }

        public ValueTask<ReadOnlyMemory<byte>?> GetInitialResponseAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        public ValueTask<ReadOnlyMemory<byte>> RespondAsync(ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken = default)
        {
            string answer = _mechanism.WrongAnswer ? "wrong" : Encoding.ASCII.GetString(challenge.Span) + "-answer";

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.ASCII.GetBytes(answer));
        }

        public ValueTask CompleteAsync(ReadOnlyMemory<byte> additionalData, CancellationToken cancellationToken = default)
        {
            _mechanism.CompletedWith = Encoding.ASCII.GetString(additionalData.Span);
            return ValueTask.CompletedTask;
        }
    }
}
