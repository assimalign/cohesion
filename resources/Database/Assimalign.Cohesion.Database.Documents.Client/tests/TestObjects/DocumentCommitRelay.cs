using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

internal sealed class DocumentCommitRelay : IAsyncDisposable
{
    private readonly DocumentClientTestHarness _harness;
    private readonly string _drop;
    private readonly CancellationTokenSource _stop;
    private readonly Task _pump;
    private int _connections;
    private int _commits;
    private int _completions;
    private int _drops;

    private DocumentCommitRelay(DocumentClientTestHarness harness, string drop, IDuplexConnectionListener listener, IDuplexConnectionFactory factory)
    {
        _harness = harness;
        _drop = drop;
        Listener = listener;
        Factory = factory;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
        _pump = RunAsync();
    }

    internal IDuplexConnectionListener Listener { get; }
    internal IDuplexConnectionFactory Factory { get; }
    internal int Connections => Volatile.Read(ref _connections);
    internal int Commits => Volatile.Read(ref _commits);
    internal int Completions => Volatile.Read(ref _completions);
    internal int Drops => Volatile.Read(ref _drops);

    internal static async Task<DocumentCommitRelay> StartAsync(DocumentClientTestHarness harness, bool tcp, string drop)
    {
        IDuplexConnectionListener listener;
        IDuplexConnectionFactory factory;
        if (tcp)
        {
            listener = new TcpConnectionListener(new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            factory = new TcpConnectionFactory();
        }
        else
        {
            var memory = new InMemoryConnectionListener();
            listener = memory;
            factory = memory.CreateFactory();
        }
        try
        {
            await listener.BindAsync(harness.Token);
            return new(harness, drop, listener, factory);
        }
        catch { await listener.DisposeAsync(); throw; }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await using var downstream = await Listener.AcceptAsync(_stop.Token);
                await using var upstream = await _harness.Factory.ConnectAsync(_harness.Listener.EndPoint, _stop.Token);
                Interlocked.Increment(ref _connections);
                await using var client = new ProtocolChannel(downstream.AsStream(), DocumentProtocol.Family, leaveOpen: true);
                await using var server = new ProtocolChannel(upstream.AsStream(), DocumentProtocol.Family, leaveOpen: true);
                using var pair = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                int committing = 0;
                Task requests = ForwardAsync(client, server, false);
                Task replies = ForwardAsync(server, client, true);
                try
                {
                    Task completed = await Task.WhenAny(requests, replies);
                    await completed;
                }
                finally
                {
                    await pair.CancelAsync();
                    try { await Task.WhenAll(requests, replies); }
                    catch (OperationCanceledException) when (pair.IsCancellationRequested) { }
                }

                async Task ForwardAsync(ProtocolChannel source, ProtocolChannel destination, bool response)
                {
                    while (await source.Reader.ReadFrameAsync(pair.Token) is { } frame)
                    {
                        if (!response && (byte)frame.Type == 64 &&
                            DocumentProtocolExecuteMessage.Decode(frame.Payload.Span).Statement == "COMMIT")
                        {
                            Interlocked.Increment(ref _commits);
                            Volatile.Write(ref committing, 1);
                        }
                        if (response && Volatile.Read(ref committing) != 0)
                        {
                            bool selected = _drop == "complete" ? (byte)frame.Type == 66 : frame.Type == ProtocolMessageType.Ready;
                            if (selected && Interlocked.CompareExchange(ref _drops, 1, 0) == 0)
                            {
                                // The selected frame is consumed here, before the client can read it.
                                if (_drop == "ready-timeout") { await Task.Delay(Timeout.Infinite, pair.Token); }
                                else { await downstream.Output.CompleteAsync(); }
                                return;
                            }
                            if ((byte)frame.Type == 66) { Interlocked.Increment(ref _completions); }
                        }
                        await destination.Writer.WriteFrameAsync(frame, pair.Token);
                        await destination.Writer.FlushAsync(pair.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _pump; }
        finally
        {
            await Listener.DisposeAsync();
            _stop.Dispose();
        }
    }
}
