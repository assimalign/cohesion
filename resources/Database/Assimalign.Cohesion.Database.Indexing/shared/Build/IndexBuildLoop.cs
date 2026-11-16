using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Deviates from the repo namespace-matches-assembly rule: stateless Indexing build
// orchestration is compiled into each model; no instance crosses an assembly boundary.
namespace Assimalign.Cohesion.Database.Indexing.Internal;

internal static class IndexBuildLoop
{
    internal const int ChunkSize = 64;

    // The model supplies durable apply and checkpoint admission, retaining its writer
    // reservation throughout. Publication happens once, after every chunk succeeds.
    // Source-owned memory is charged before retaining a row in the bounded array.
    internal static async ValueTask RunAsync<T>(IEnumerable<T> source,
        Func<T[], CancellationToken, ValueTask> apply, Func<CancellationToken, ValueTask> publish,
        CancellationToken cancellationToken, Func<IDisposable>? beginChunk = null, Action<T>? retain = null)
    {
        using var iterator = source.GetEnumerator();
        while (iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var memory = beginChunk?.Invoke();
            var chunk = new T[ChunkSize];
            int count = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                retain?.Invoke(iterator.Current);
                chunk[count++] = iterator.Current;
            } while (count < chunk.Length && iterator.MoveNext());
            if (count != chunk.Length) { Array.Resize(ref chunk, count); }
            await apply(chunk, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await publish(cancellationToken).ConfigureAwait(false);
    }
}
