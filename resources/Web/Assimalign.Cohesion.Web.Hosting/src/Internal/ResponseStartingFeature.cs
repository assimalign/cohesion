using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>Callback registrations driven by the transport's existing head-commit hook.</summary>
internal sealed class ResponseStartingFeature : IWebResponseStartingFeature
{
    private readonly HttpExchangeInterceptorResponseContext _context;
    private IHttpContext? _exchange;
    private List<(Func<object?, Task> Callback, object? State)>? _callbacks;
    private HashSet<HttpHeaderKey>? _preservedHeaders;
    private Dictionary<HttpHeaderKey, HttpHeaderValue>? _headerChanges;
    private bool _registrationClosed;

    public ResponseStartingFeature(HttpExchangeInterceptorResponseContext context)
    {
        _context = context;
    }

    internal void Bind(IHttpContext exchange) => _exchange = exchange;

    public string Name => nameof(IWebResponseStartingFeature);

    internal bool HasFailed { get; private set; }

    public void OnStarting(Func<object?, Task> callback, object? state,
        IReadOnlyCollection<HttpHeaderKey>? preservedHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        InvalidOperationException.ThrowIf(_registrationClosed || _context.Control is { HasResponseStarted: true },
            "Response-starting callbacks cannot be registered after response starting has begun.");
        (_callbacks ??= []).Add((callback, state));
        if (preservedHeaders is not null)
        {
            foreach (HttpHeaderKey key in preservedHeaders)
            {
                (_preservedHeaders ??= []).Add(key);
            }
        }
    }

    internal async ValueTask InvokeAsync(CancellationToken cancellationToken = default)
    {
        // This closes registration, not response-start tracking: only the transport decides
        // whether a head committed and invokes this method through its once-per-exchange gate.
        _registrationClosed = true;
        List<(Func<object?, Task> Callback, object? State)>? callbacks = _callbacks;
        _callbacks = null;
        if (callbacks is null)
        {
            return;
        }

        IHttpHeaderCollection headers = _context.Headers;
        Dictionary<HttpHeaderKey, HttpHeaderValue> before = new(headers);
        try
        {
            for (int index = callbacks.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (Func<object?, Task> callback, object? state) = callbacks[index];
                await callback(state).ConfigureAwait(false);
            }
        }
        // Deviates from the specific-exception rule: user callbacks can throw any exception.
        // The transport's once-only hook must never permit a retry to send an incomplete head.
        catch (Exception)
        {
            HasFailed = true;
            try
            {
                _exchange?.Cancel();
            }
            // Cancellation callbacks are also application code; preserve the starting failure.
            catch (Exception)
            {
            }
            throw;
        }

        // Retain callback edits and declared policy fields, not all refused application fields.
        // Empty values represent removals. No snapshot is allocated on the no-callback path.
        foreach ((HttpHeaderKey key, HttpHeaderValue value) in headers)
        {
            if (!before.TryGetValue(key, out HttpHeaderValue original) || !value.Equals(original))
            {
                (_headerChanges ??= new())[key] = value;
            }
            before.Remove(key);
        }
        foreach (HttpHeaderKey key in before.Keys)
        {
            (_headerChanges ??= new())[key] = HttpHeaderValue.Empty;
        }

        // A policy can accept an existing value without editing it (for example no-clobber
        // framing). Capture declared keys after every callback, including deliberate absence.
        if (_preservedHeaders is not null)
        {
            foreach (HttpHeaderKey key in _preservedHeaders)
            {
                (_headerChanges ??= new())[key] = headers.TryGetValue(key, out HttpHeaderValue value)
                    ? value
                    : HttpHeaderValue.Empty;
            }
            _preservedHeaders = null;
        }
    }

    internal void RestoreHeaders()
    {
        if (_context.Control is { HasResponseStarted: true } || _headerChanges is null)
        {
            return;
        }

        foreach ((HttpHeaderKey key, HttpHeaderValue value) in _headerChanges)
        {
            _context.Headers[key] = value;
        }
    }
}
