using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Security.Tests;

/// <summary>
/// Provides test helper extension members for <see cref="PipeReader"/>.
/// </summary>
internal static class PipeReaderTestExtensions
{
    extension(PipeReader reader)
    {
        /// <summary>
        /// Reads until exactly <paramref name="count"/> bytes are available, returns them, and
        /// advances the reader past them.
        /// </summary>
        /// <param name="count">The number of bytes to read.</param>
        /// <param name="cancellationToken">A token to cancel the read.</param>
        /// <returns>The bytes read.</returns>
        public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);

                if (result.Buffer.Length >= count)
                {
                    ReadOnlySequence<byte> consumed = result.Buffer.Slice(0, count);
                    byte[] bytes = consumed.ToArray();
                    reader.AdvanceTo(consumed.End);
                    return bytes;
                }

                if (result.IsCompleted)
                {
                    throw new InvalidOperationException($"The reader completed after {result.Buffer.Length} bytes; expected {count}.");
                }

                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            }
        }
    }
}
