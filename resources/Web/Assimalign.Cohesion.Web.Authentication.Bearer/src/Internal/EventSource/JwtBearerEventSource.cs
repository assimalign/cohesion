using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Authentication.Bearer.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Authentication.Bearer")]
internal sealed class JwtBearerEventSource : EventSource
{
    public static readonly JwtBearerEventSource Log = new();

    private long _rejectedTokens;
    private PollingCounter? _rejectedTokensCounter;

    private JwtBearerEventSource() { }

    internal long RejectedTokens => Interlocked.Read(ref _rejectedTokens);

    [NonEvent]
    public void TokenRejected(IHttpContext context, string scheme, string reason, JsonWebToken? jwt, TokenValidationResult? validation)
    {
        Interlocked.Increment(ref _rejectedTokens);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            TokenRejected(scheme, reason, Truncate(jwt?.Algorithm, 32), Truncate(jwt?.Header.KeyId, 128),
                validation is { Errors.Count: > 0 } ? validation.Errors[0].Code : string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Bearer scheme {0} rejected a token: {1}")]
    private void TokenRejected(string scheme, string reason, string algorithm, string keyId, string validationCode, string requestId)
        => WriteEvent(1, scheme, reason, algorithm, keyId, validationCode, requestId);

    [NonEvent]
    private static string Truncate(string? value, int limit)
        => value is null ? string.Empty : value.Length <= limit ? value : value[..limit];

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedTokensCounter ??= new PollingCounter("rejected-tokens", this, () => RejectedTokens)
            {
                DisplayName = "Rejected bearer tokens",
            };
        }
    }
}
