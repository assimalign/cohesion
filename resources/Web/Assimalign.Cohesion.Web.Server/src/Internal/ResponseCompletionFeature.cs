using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// Carries work that must run only after the current response has been written to its transport.
/// </summary>
/// <remarks>
/// The implementation is private to Web.Server and installed per exchange by
/// <see cref="WebApplicationServer"/>. Keeping the callbacks on the exchange lets terminal
/// middleware defer host-lifetime signals without coupling the HTTP transport to Hosting.
/// </remarks>
internal sealed class ResponseCompletionFeature : IWebResponseCompletionFeature
{
    private readonly List<Func<ValueTask>> _callbacks = new();
    private bool _isCompleted;

    public string Name => nameof(ResponseCompletionFeature);

    public void Register(Func<ValueTask> callback)
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

        List<Exception>? failures = null;
        foreach (Func<ValueTask> callback in _callbacks)
        {
            try
            {
                await callback().ConfigureAwait(false);
            }
            // Deviates from the repo "catch specific exceptions" rule: arbitrary application
            // callbacks are isolated, including synchronous throws and asynchronous cancellation.
            catch (Exception exception)
            {
                (failures ??= new()).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(ErrorMessages.ResponseCompletionCallbacksFailed, failures);
        }
    }
}
