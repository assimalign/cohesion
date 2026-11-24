using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("DatabaseApplicationOwnership")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseApplicationOwnership.DebugView))]
internal sealed class DatabaseApplicationOwnership
{
    // The content root, the configuration, the logger factory and the service provider, in creation
    // order. The provider owns every engine and lifecycle service its factories created and disposes
    // them in reverse creation order, services before the engines they use.
    // The provider continues past a failing product and rethrows one failure as itself, or several as
    // one AggregateException, which is kept whole: a single product's own aggregate is indistinguishable
    // from it.
    internal readonly List<object> Infrastructure = [];

    internal async Task DisposeAsync(List<Exception> failures)
    {
        await DisposeReverseAsync(Infrastructure, failures).ConfigureAwait(false);
    }

    private static async Task DisposeReverseAsync<T>(IReadOnlyList<T> products, List<Exception> failures)
    {
        for (int index = products.Count - 1; index >= 0; index--)
        {
            try
            {
                // Configuration and its manager expose disposal as a concrete member rather than
                // implementing IAsyncDisposable; retain that owning contract explicitly.
                if (products[index] is Assimalign.Cohesion.Configuration.ConfigurationManager manager)
                {
                    await manager.DisposeAsync().ConfigureAwait(false);
                }
                else if (products[index] is Assimalign.Cohesion.Configuration.Configuration configuration)
                {
                    await configuration.DisposeAsync().ConfigureAwait(false);
                }
                else if (products[index] is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (products[index] is IDisposable synchronous)
                {
                    synchronous.Dispose();
                }
            }
            // Cleanup must attempt all independent owned roots, retaining every failure.
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseApplicationOwnership value)
        {
            _ = value;
        }
    }
}
