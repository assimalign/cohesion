using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;

public static class TransportExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="transport"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async IAsyncEnumerable<ITransportConnection> EnumerateAsync(
        this ITransport transport, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(transport);

        while (true)
        {
            yield return await transport.InitializeAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
