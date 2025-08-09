using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;

public static class TransportExtensions
{

    extension(ITransport transport)
    {
        public async IAsyncEnumerable<ITransportConnection> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    extension(ITransportConnection connection)
    {
        public bool IsOpen() => connection.State == ConnectionState.Open;
    }

    extension(ITransportConnectionContext context)
    {
        public void AddItem<T>(string key, T value)
        {
            ThrowHelper.ThrowIfNull(context);
            ThrowHelper.ThrowIfNullOrEmpty(key);
            context.Items[key] = value;
        }

        public T? GetItem<T>(string key) where T : class
        {
            ThrowHelper.ThrowIfNull(context);
            ThrowHelper.ThrowIfNullOrEmpty(key);

            if (context.Items.TryGetValue(key, out var value))
            {
                return value as T;
            }
            return default;
        }
    }
}
