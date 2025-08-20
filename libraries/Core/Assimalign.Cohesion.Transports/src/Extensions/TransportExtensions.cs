using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using System.Buffers;
using System.IO.Pipelines;

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

    extension(ITransportConnectionPipe pipe)
    {

        public async ValueTask<ReadResult> PeekAsync(CancellationToken cancellationToken = default)
        {
            ReadResult result = await pipe.Input.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            pipe.Input.AdvanceTo(buffer.Start);

            return result;
        }

        public async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadResult result = await pipe.Input.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            pipe.Input.AdvanceTo(
                buffer.Start,
                buffer.End);

            return result;
        }

        public async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await pipe.Output.WriteAsync(buffer);

            //Output.Advance(buffer.Length);

            return result;
        }
    }
}
