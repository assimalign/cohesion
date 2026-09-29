using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

internal sealed class DatabaseApplicationOwnership
{
    internal readonly List<object> Infrastructure = [];
    internal readonly List<IDatabaseEngine> Engines = [];
    internal readonly List<object> Services = [];

    internal async Task DisposeAsync(List<Exception> failures)
    {
        await DisposeReverseAsync(Services, failures).ConfigureAwait(false);
        await DisposeReverseAsync(Engines, failures).ConfigureAwait(false);
        await DisposeReverseAsync(Infrastructure, failures).ConfigureAwait(false);
    }

    private static async Task DisposeReverseAsync<T>(IReadOnlyList<T> products, List<Exception> failures)
    {
        for (int index = products.Count - 1; index >= 0; index--)
        {
            try
            {
                // Configuration exposes disposal as a concrete member rather than
                // implementing IAsyncDisposable; retain that owning contract explicitly.
                if (products[index] is Assimalign.Cohesion.Configuration.Configuration configuration)
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
}
