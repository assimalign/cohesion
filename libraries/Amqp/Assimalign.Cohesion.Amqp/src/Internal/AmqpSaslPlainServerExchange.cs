using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Internal;

/// <summary>
/// The server exchange of <c>PLAIN</c>: it parses <c>[authzid] NUL authcid NUL passwd</c> (RFC 4616,
/// section 2) and asks the application delegate to validate it.
/// </summary>
/// <remarks>
/// A client whose <c>sasl-init</c> carried no initial response gets one empty challenge; a second missing
/// or malformed message fails the exchange with <see cref="AmqpSaslCode.Auth"/>.
/// </remarks>
internal sealed class AmqpSaslPlainServerExchange : IAmqpSaslServerExchange
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Func<AmqpSaslPlainCredentials, CancellationToken, ValueTask<bool>> _validate;
    private bool _challenged;

    public AmqpSaslPlainServerExchange(Func<AmqpSaslPlainCredentials, CancellationToken, ValueTask<bool>> validate)
    {
        _validate = validate;
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

        if (!TryParse(response.Value.Span, out AmqpSaslPlainCredentials? credentials))
        {
            return AmqpSaslServerStep.Fail();
        }

        bool valid = await _validate(credentials, cancellationToken).ConfigureAwait(false);

        return valid
            ? AmqpSaslServerStep.Success(credentials.AuthorizationId ?? credentials.UserName)
            : AmqpSaslServerStep.Fail();
    }

    private static bool TryParse(ReadOnlySpan<byte> message, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AmqpSaslPlainCredentials? credentials)
    {
        credentials = null;

        int first = message.IndexOf((byte)0);
        if (first < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> rest = message[(first + 1)..];
        int second = rest.IndexOf((byte)0);
        if (second < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> authorizationId = message[..first];
        ReadOnlySpan<byte> userName = rest[..second];
        ReadOnlySpan<byte> password = rest[(second + 1)..];

        // RFC 4616: authcid and passwd are 1 to 255 octets and contain no NUL.
        if (userName.IsEmpty || password.IsEmpty || userName.Length > 255 || password.Length > 255 || password.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            credentials = new AmqpSaslPlainCredentials(
                authorizationId.IsEmpty ? null : _strictUtf8.GetString(authorizationId),
                _strictUtf8.GetString(userName),
                _strictUtf8.GetString(password));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
