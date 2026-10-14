using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

internal sealed class OutputCacheCompletionFeature : IWebResponseCompletionFeature
{
    private readonly List<Func<ValueTask>> _callbacks = new();

    public string Name => nameof(OutputCacheCompletionFeature);

    public void Register(Func<ValueTask> callback) => _callbacks.Add(callback);

    public async ValueTask CompleteAsync()
    {
        foreach (Func<ValueTask> callback in _callbacks)
        {
            await callback();
        }
    }
}
