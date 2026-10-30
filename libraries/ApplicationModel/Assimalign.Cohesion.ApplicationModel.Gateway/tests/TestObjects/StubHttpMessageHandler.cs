using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Answers every request with one scripted status and body, and records each request.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpMessageHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public List<(HttpMethod Method, Uri Uri, string? ContentType, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType, body));
        }

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}
