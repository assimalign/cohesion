using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Internal;

/// <summary>
/// The server exchange of <c>EXTERNAL</c>: the client's message is the authorization identity it asks for
/// (empty for none), and the application delegate maps the carrier's established identity to the
/// authenticated one.
/// </summary>
[DebuggerDisplay("AmqpSaslExternalServerExchange")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpSaslExternalServerExchange : IAmqpSaslServerExchange
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IConnection _connection;
    private readonly Func<IConnection, string?, CancellationToken, ValueTask<string?>> _authenticate;
    private bool _challenged;

    public AmqpSaslExternalServerExchange(IConnection connection, Func<IConnection, string?, CancellationToken, ValueTask<string?>> authenticate)
    {
        _connection = connection;
        _authenticate = authenticate;
    }

    public async ValueTask<AmqpSaslServerStep> EvaluateAsync(ReadOnlyMemory<byte>? response, CancellationToken cancellationToken = default)
    {
        if (response is null)
        {
            if (_challenged)
            {
                return AmqpSaslServerStep.Fail();
            }

            _challenged = true;
            return AmqpSaslServerStep.Challenge(ReadOnlyMemory<byte>.Empty);
        }

        string? authorizationId;
        try
        {
            authorizationId = response.Value.IsEmpty ? null : _strictUtf8.GetString(response.Value.Span);
        }
        catch (DecoderFallbackException)
        {
            return AmqpSaslServerStep.Fail();
        }

        string? identity = await _authenticate(_connection, authorizationId, cancellationToken).ConfigureAwait(false);

        return identity is null ? AmqpSaslServerStep.Fail() : AmqpSaslServerStep.Success(identity);
    }

    private sealed class DebugView
    {
        public DebugView(AmqpSaslExternalServerExchange exchange)
        {
        }
    }
}
