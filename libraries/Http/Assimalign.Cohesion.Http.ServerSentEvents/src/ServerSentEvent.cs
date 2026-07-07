using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single Server-Sent Events message — the <c>SseItem</c>-equivalent event
/// model for the <c>text/event-stream</c> wire format defined by the WHATWG HTML
/// Server-Sent Events specification.
/// </summary>
/// <remarks>
/// <para>
/// An event carries any of four data-bearing fields (<c>event</c>, <c>data</c>,
/// <c>id</c>, <c>retry</c>) plus an optional comment. All are optional: a message
/// with only <see cref="Data"/> is the common case; a message with only
/// <see cref="Comment"/> is a keep-alive heartbeat that a client ignores. The
/// value object holds the fields; <see cref="ServerSentEventFormatter"/> renders
/// them to the wire format.
/// </para>
/// <para>
/// <see cref="EventType"/>, <see cref="Id"/>, and <see cref="Comment"/> are
/// single-line field values and MUST NOT contain a line break — the wire format
/// terminates a field at the first newline. <see cref="Data"/> MAY contain
/// newlines: each line is emitted as its own <c>data:</c> field and the client
/// rejoins them with <c>\n</c>.
/// </para>
/// </remarks>
public readonly struct ServerSentEvent
{
    /// <summary>The media type of a Server-Sent Events response body.</summary>
    public const string MediaType = "text/event-stream";

    /// <summary>
    /// Initializes a new event carrying the supplied <paramref name="data"/> payload.
    /// </summary>
    /// <param name="data">The event data (the <c>data</c> field). May be multi-line or <see langword="null"/>.</param>
    public ServerSentEvent(string? data)
    {
        Data = data;
    }

    /// <summary>
    /// Gets the event type (the <c>event</c> field). When <see langword="null"/>
    /// or empty the client dispatches the message as a generic <c>message</c>
    /// event. Must be single-line.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Gets the last-event-id (the <c>id</c> field). The client echoes the most
    /// recent id back in the <c>Last-Event-ID</c> request header on reconnect.
    /// Must be single-line. An empty (non-null) value resets the client's stored
    /// id.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Gets the event data (the <c>data</c> field). May contain newlines; each
    /// line is serialized as a separate <c>data:</c> field. <see langword="null"/>
    /// emits no data field.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>
    /// Gets the reconnection time the client should use (the <c>retry</c> field),
    /// serialized as whole milliseconds. <see langword="null"/> emits no retry field.
    /// </summary>
    public TimeSpan? Retry { get; init; }

    /// <summary>
    /// Gets an optional comment (a line beginning with <c>:</c>). Comments are
    /// ignored by clients and are the mechanism used for keep-alive heartbeats.
    /// Must be single-line.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Creates a generic <c>message</c> event carrying <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The event data payload.</param>
    /// <returns>An event with its <see cref="Data"/> set.</returns>
    public static ServerSentEvent Message(string? data) => new(data);

    /// <summary>
    /// Creates a keep-alive heartbeat — an event carrying only a comment, which a
    /// client ignores while the bytes keep the connection and any intermediaries
    /// from timing the idle stream out.
    /// </summary>
    /// <param name="comment">The comment text. Defaults to <c>keep-alive</c>.</param>
    /// <returns>An event with only its <see cref="Comment"/> set.</returns>
    public static ServerSentEvent KeepAlive(string comment = "keep-alive") => new() { Comment = comment };
}
