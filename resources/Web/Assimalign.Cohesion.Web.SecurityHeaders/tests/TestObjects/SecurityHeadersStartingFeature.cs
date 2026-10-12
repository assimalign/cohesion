using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

internal sealed class SecurityHeadersStartingFeature : IWebResponseStartingFeature
{
    private Func<object?, Task>? _callback;
    private object? _state;

    public string Name => nameof(IWebResponseStartingFeature);

    public void OnStarting(Func<object?, Task> callback, object? state, IReadOnlyCollection<HttpHeaderKey>? preservedHeaders = null)
    {
        _callback = callback;
        _state = state;
    }

    public Task InvokeAsync() => _callback!(_state);
}
