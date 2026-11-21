using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A stand-in for a TLS layer, so a scripted peer can play in-band TLS (AMQP 1.0 Part 5, section 5.2) as bytes: its
/// handshake writes <see cref="Hello"/>, reads the same eight bytes back, and returns a
/// <see cref="TestSecuredConnection"/> over the connection it was given.
/// </summary>
/// <remarks>
/// Both sides of the stand-in handshake write first, so a script expects the hello and writes it, in either order, and
/// the bytes after it are the secured connection's. A peer that answers with other bytes fails the handshake with an
/// <see cref="AuthenticationException"/>, as a failed TLS handshake does.
/// </remarks>
internal sealed class AmqpTestTlsLayer : IConnectionLayer
{
    private readonly ConcurrentQueue<TestSecuredConnection> _secured = new();

    /// <summary>
    /// Gets the eight bytes each side of the stand-in handshake writes and expects.
    /// </summary>
    public static byte[] Hello { get; } = "TLS-TEST"u8.ToArray();

    /// <summary>
    /// Gets the connections the layer returned, in the order it returned them.
    /// </summary>
    public TestSecuredConnection[] Secured => _secured.ToArray();

    public ConnectionCapabilities Describe(ConnectionCapabilities capabilities)
    {
        return capabilities with { Security = ConnectionSecurity.Tls };
    }

    public async ValueTask<IDuplexConnection> UpgradeAsync(IDuplexConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.Output.WriteAsync(Hello, cancellationToken);

        PipeReader input = connection.Input;

        while (true)
        {
            ReadResult result = await input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= Hello.Length)
            {
                byte[] answer = buffer.Slice(0, Hello.Length).ToArray();

                // Bytes behind the hello belong to the secured connection.
                input.AdvanceTo(buffer.GetPosition(Hello.Length));

                if (!answer.AsSpan().SequenceEqual(Hello))
                {
                    throw new AuthenticationException("The peer did not answer the stand-in TLS handshake.");
                }

                TestSecuredConnection secured = new(connection);

                _secured.Enqueue(secured);
                return secured;
            }

            input.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
            {
                throw new IOException("The peer hung up during the stand-in TLS handshake.");
            }
        }
    }
}
