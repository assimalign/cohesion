using System;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// A hand-driven protocol client over one in-memory connection, so the server
/// tests exercise the wire contract directly.
/// </summary>
internal sealed class KeyValueProtocolClient : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IProtocolFrameReader _reader;
    private readonly IProtocolFrameWriter _writer;

    internal KeyValueProtocolClient(IConnection connection)
    {
        _connection = connection;
        var stream = connection.AsStream();
        _reader = ProtocolFraming.CreateReader(stream, leaveOpen: true);
        _writer = ProtocolFraming.CreateWriter(stream, leaveOpen: true);
    }

    public async Task SendAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload = default)
    {
        await _writer.WriteFrameAsync(new ProtocolFrame(type, payload), TestTimeout.Token());
        await _writer.FlushAsync(TestTimeout.Token());
    }

    public async Task<ProtocolFrame?> ReadAsync(int timeoutSeconds = 10)
        => await _reader.ReadFrameAsync(TestTimeout.Token(timeoutSeconds));

    /// <summary>
    /// Reads the next frame, asserting the stream did not end.
    /// </summary>
    public async Task<ProtocolFrame> ExpectAsync(ProtocolMessageType type)
    {
        ProtocolFrame? frame = await ReadAsync();

        frame.ShouldNotBeNull();
        frame.Value.Type.ShouldBe(type);
        return frame.Value;
    }

    /// <summary>
    /// Runs the startup → authenticate → ready handshake to completion.
    /// </summary>
    public async Task HandshakeAsync(string database = KeyValueServerHarness.DatabaseName, string principal = "tester")
    {
        await SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, database, principal).Encode());
        await ExpectAsync(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse);
        await ExpectAsync(ProtocolMessageType.Ready);
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.DisposeAsync();
        await _writer.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
