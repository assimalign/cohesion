using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using System.Buffers;
using System.IO.Pipelines;

/// <summary>
/// Provides convenience helpers for transport types, contexts, and pipes.
/// </summary>
public static class TransportExtensions
{
    extension(ITransport transport)
    {
        public async IAsyncEnumerable<ITransportConnection> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(transport);

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
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNullOrEmpty(key);

            context.Items[key] = value;
        }

        public T? GetItem<T>(string key) where T : class
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNullOrEmpty(key);

            if (context.Items.TryGetValue(key, out var value))
            {
                return value as T;
            }
            return default;
        }
    }

    extension(ITransportConnectionPipe pipe)
    {
        /// <summary>
        /// Reads the current input buffer without consuming it so the next read can observe the same data.
        /// </summary>
        /// <param name="cancellationToken">A token that cancels the read operation.</param>
        /// <returns>The current read result for the pipe input.</returns>
        public async ValueTask<ReadResult> PeekAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pipe);

            ReadResult result = await pipe.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            pipe.Input.AdvanceTo(buffer.Start);

            return result;
        }

        /// <summary>
        /// Reads the current input buffer, consumes the returned bytes, and returns a stable snapshot of the data.
        /// </summary>
        /// <param name="cancellationToken">A token that cancels the read operation.</param>
        /// <returns>The current read result for the pipe input.</returns>
        public async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pipe);

            ReadResult result = await pipe.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            ReadResult snapshot = new ReadResult(
                new ReadOnlySequence<byte>(buffer.ToArray()),
                result.IsCanceled,
                result.IsCompleted);

            pipe.Input.AdvanceTo(
                buffer.End,
                buffer.End);

            return snapshot;
        }

        /// <summary>
        /// Writes the provided buffer to the pipe output and flushes it.
        /// </summary>
        /// <param name="buffer">The bytes to write.</param>
        /// <param name="cancellationToken">A token that cancels the write operation.</param>
        /// <returns>The flush result for the write operation.</returns>
        public async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pipe);

            var result = await pipe.Output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            return result;
        }
    }
}
