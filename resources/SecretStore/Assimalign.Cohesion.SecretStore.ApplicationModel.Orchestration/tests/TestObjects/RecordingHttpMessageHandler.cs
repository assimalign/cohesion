using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Records every request the SecretStore client sends and answers it from a caller-supplied
/// responder, so tests can assert the exact routes, headers, and bodies a provider produces.
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _respond;
    private readonly List<RecordedRequest> _requests = new();
    private readonly object _gate = new();

    internal RecordingHttpMessageHandler(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    internal IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    internal static HttpResponseMessage Bytes(byte[] content, string mediaType = "application/octet-stream")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return response;
    }

    internal static HttpResponseMessage Pem(string pem) =>
        Bytes(Encoding.UTF8.GetBytes(pem), "application/x-pem-file");

    internal static HttpResponseMessage Status(HttpStatusCode status) => new(status)
    {
        Content = new ByteArrayContent(Array.Empty<byte>()),
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.Accept.Select(static value => value.MediaType ?? string.Empty).ToArray(),
            request.Content?.Headers.ContentType?.MediaType,
            body);
        lock (_gate)
        {
            _requests.Add(recorded);
        }

        HttpResponseMessage response = _respond(recorded);
        response.RequestMessage = request;
        return response;
    }
}
