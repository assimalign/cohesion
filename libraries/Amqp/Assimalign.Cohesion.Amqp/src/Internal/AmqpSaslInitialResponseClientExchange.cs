using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Internal;

/// <summary>
/// The client exchange of a single-message mechanism (<c>PLAIN</c>, <c>ANONYMOUS</c>, <c>EXTERNAL</c>): it
/// sends one message as the initial response and answers any challenge with a fixed response.
/// </summary>
internal sealed class AmqpSaslInitialResponseClientExchange : IAmqpSaslClientExchange
{
    private readonly ReadOnlyMemory<byte> _initialResponse;
    private readonly ReadOnlyMemory<byte> _challengeResponse;

    public AmqpSaslInitialResponseClientExchange(ReadOnlyMemory<byte> initialResponse, ReadOnlyMemory<byte> challengeResponse)
    {
        _initialResponse = initialResponse;
        _challengeResponse = challengeResponse;
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetInitialResponseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(_initialResponse);
    }

    public ValueTask<ReadOnlyMemory<byte>> RespondAsync(ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_challengeResponse);
    }

    public ValueTask CompleteAsync(ReadOnlyMemory<byte> additionalData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.CompletedTask;
    }
}
