using System;
using System.Diagnostics.Tracing;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Authentication.Cookie")]
internal sealed class CookieAuthenticationEventSource : EventSource
{
    public static readonly CookieAuthenticationEventSource Log = new();
    private const string repositoryReadFailure = "The key repository could not be read to resolve the key that produced this payload.";

    private long _rejectedTickets;
    private PollingCounter? _rejectedTicketsCounter;

    private CookieAuthenticationEventSource() { }

    internal long RejectedTickets => Interlocked.Read(ref _rejectedTickets);

    [NonEvent]
    public void TicketRejected(IHttpContext context, string scheme, string reason, Exception? exception)
    {
        Interlocked.Increment(ref _rejectedTickets);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            // Custom protectors may echo credentials. Only the built-in fixed messages are safe.
            string message = (exception is DataProtectionException ? exception.Message : null) switch
            {
                "The protected payload is malformed." => "The protected payload is malformed.",
                "The protected payload could not be verified." => "The protected payload could not be verified.",
                "The key that produced this payload is unknown to the ring." => "The key that produced this payload is unknown to the ring.",
                "The key that produced this payload has been revoked." => "The key that produced this payload has been revoked.",
                "The key that produced this payload has aged out of the unprotect grace window." => "The key that produced this payload has aged out of the unprotect grace window.",
                repositoryReadFailure => repositoryReadFailure,
                _ => string.Empty,
            };
            TicketRejected(scheme, reason, exception?.GetType().FullName ?? string.Empty, message,
                message == repositoryReadFailure ? exception?.InnerException?.GetType().FullName ?? string.Empty : string.Empty,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Cookie scheme {0} rejected a ticket: {1}")]
    private void TicketRejected(string scheme, string reason, string exceptionType, string exceptionMessage, string innerExceptionType, string requestId)
        => WriteEvent(1, scheme, reason, exceptionType, exceptionMessage, innerExceptionType, requestId);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _rejectedTicketsCounter ??= new PollingCounter("rejected-tickets", this, () => RejectedTickets)
            {
                DisplayName = "Rejected authentication tickets",
            };
        }
    }
}
