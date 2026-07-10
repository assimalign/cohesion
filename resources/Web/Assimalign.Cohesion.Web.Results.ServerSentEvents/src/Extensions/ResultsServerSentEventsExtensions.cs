using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// Grafts the Server-Sent Events factory onto <see cref="Results"/> as a static extension member,
/// so referencing this adapter package extends the core factory surface in place —
/// <c>Results.ServerSentEvents(...)</c> reads exactly like the built-ins that live in the core
/// package. The <see cref="TypedResults"/> counterpart lives in
/// <see cref="TypedResultsServerSentEventsExtensions"/> (the two containers are separate because
/// both factories lower to identically-shaped static methods).
/// </summary>
public static class ResultsServerSentEventsExtensions
{
    extension(Results)
    {
        /// <summary>
        /// Creates a Server-Sent Events result that streams <paramref name="events"/> as a
        /// <c>text/event-stream</c> response, flushing each event so the client observes it
        /// immediately. Fails loudly with <see cref="NotSupportedException"/> at execution when
        /// streaming is not enabled on the exchange; never sets <c>Content-Length</c>.
        /// </summary>
        /// <param name="events">The event sequence to stream. Enumerated once per execution.</param>
        /// <returns>The Server-Sent Events result.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="events"/> is <see langword="null"/>.</exception>
        public static IResult ServerSentEvents(IAsyncEnumerable<ServerSentEvent> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            return new ServerSentEventsHttpResult(events);
        }
    }
}
