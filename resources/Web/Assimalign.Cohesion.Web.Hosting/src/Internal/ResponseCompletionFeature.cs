using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Carries work that must run only after the current response has been written to its transport.
/// </summary>
/// <remarks>
/// The feature is private to Web.Hosting and installed per exchange by
/// <see cref="WebApplicationServer"/>. Keeping the callbacks on the exchange lets terminal
/// middleware defer host-lifetime signals without coupling the HTTP transport to Hosting.
/// </remarks>
internal sealed class ResponseCompletionFeature : IHttpFeature
{
    private readonly List<Func<ValueTask>> _callbacks = new();
    private bool _isCompleted;

    public string Name => nameof(ResponseCompletionFeature);

    internal void Register(Func<ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        InvalidOperationException.ThrowIf(
            _isCompleted,
            "Response completion callbacks cannot be registered after the response has completed.");

        _callbacks.Add(callback);
    }

    internal async ValueTask CompleteAsync()
    {
        if (_isCompleted)
        {
            return;
        }

        _isCompleted = true;

        foreach (Func<ValueTask> callback in _callbacks)
        {
            await callback().ConfigureAwait(false);
        }
    }
}
