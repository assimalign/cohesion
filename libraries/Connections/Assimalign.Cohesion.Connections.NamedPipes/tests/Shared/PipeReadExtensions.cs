using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

/// <summary>
/// Test helpers for draining bytes from a <see cref="PipeReader"/>.
/// </summary>
internal static class PipeReadExtensions
{
    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes from the reader, failing if the pipe completes first.
    /// </summary>
    public static async Task<byte[]> ReadExactlyAsync(this PipeReader reader, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);

            if (result.Buffer.Length >= count)
            {
                byte[] bytes = result.Buffer.Slice(0, count).ToArray();

                reader.AdvanceTo(result.Buffer.GetPosition(count));

                return bytes;
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException($"The connection completed before {count} bytes were received.");
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    /// <summary>
    /// Reads from the reader until the pipe completes, returning all bytes received.
    /// </summary>
    public static async Task<byte[]> ReadToEndAsync(this PipeReader reader, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> writer = new();

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                writer.Write(segment.Span);
            }

            reader.AdvanceTo(buffer.End);

            if (result.IsCompleted)
            {
                return writer.WrittenSpan.ToArray();
            }
        }
    }
}
