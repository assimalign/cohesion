using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.ClientFactory.Tests;

// The BCL pipeline types — the enclosing Assimalign.Cohesion.Http namespace would otherwise
// shadow them with the Cohesion protocol value objects.
using HttpStatusCode = System.Net.HttpStatusCode;

/// <summary>
/// A request observed by <see cref="ScriptedRedirectHandler"/>, snapshotted at send time —
/// the redirect layer mutates the live <see cref="HttpRequestMessage"/> between hops, so each
/// hop's method, target, content, and credential presence must be captured as it happens.
/// </summary>
internal sealed record RecordedRequest(
    string Method,
    Uri? Uri,
    string? Content,
    string? ContentType,
    bool HasAuthorization);

/// <summary>
/// A scripted terminal <see cref="HttpMessageHandler"/>: answers each send with the next queued
/// response and records every request it observes. Stands in for the wire under the factory's
/// redirect layer.
/// </summary>
internal sealed class ScriptedRedirectHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();

    public List<RecordedRequest> Requests { get; } = new();

    public ScriptedRedirectHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _script.Enqueue(responder);
        return this;
    }

    public ScriptedRedirectHandler EnqueueRedirect(HttpStatusCode statusCode, string? location)
        => Enqueue(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            if (location is not null)
            {
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }
            return response;
        });

    public ScriptedRedirectHandler EnqueueOk()
        => Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? content = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri,
            content,
            request.Content?.Headers.ContentType?.ToString(),
            request.Headers.Authorization is not null));

        Func<HttpRequestMessage, HttpResponseMessage> responder = _script.Count > 0
            ? _script.Dequeue()
            : static _ => new HttpResponseMessage(HttpStatusCode.OK);

        return responder.Invoke(request);
    }
}
