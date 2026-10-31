using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Tests.TestObjects;

// Deviates from the repo namespace-matches-assembly rule: linked test support keeps its owning namespace.
// The same failure schedule runs through every real model adapter. These probes add
// ledger entries through the existing kernel surface; no shipped boundary is widened.
internal sealed class SavepointUndoFault : RecordVersionIndex, IDisposable
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoBatchApplied")]
    private static extern void ObserveBatch(RecordSpaceVersionStore store, Action? observer);

    private readonly TransactionCoordinator _coordinator;
    private readonly int _stage;
    private bool _armed = true;
    internal readonly IOException Failure = new("Injected savepoint conformance failure");
    internal int Calls;
    internal int Value = 7;

    internal SavepointUndoFault(TransactionCoordinator coordinator, TransactionContext context, int stage)
    {
        _coordinator = coordinator;
        _stage = stage;
        switch (stage)
        {
            case 0:
                coordinator.VersionStore.RecordIndexEntryCreated(context.Sequence, this, ReadOnlyMemory<byte>.Empty, 0);
                break;
            case 1:
                coordinator.VersionStore.RecordIndexEntryTombstoned(context.Sequence, this, ReadOnlyMemory<byte>.Empty, 0);
                break;
            case 2:
            case 3:
                coordinator.ApplyMemoryChange(context, this, () => Value = 42);
                break;
            default:
                ObserveBatch(coordinator.VersionStore, () =>
                {
                    Calls++;
                    if (_armed && Calls == stage - 3) { throw Failure; }
                });
                break;
        }
    }

    internal static IEnumerable<object[]> Cases()
    {
        // Index erase, index clear, memory before/after restoration, physical
        // first/second/third bracket. Recovery uses the target, its parent or root.
        for (int stage = 0; stage < 7; stage++)
        { for (int recovery = 0; recovery < 3; recovery++) { yield return [stage, recovery]; } }
    }

    internal bool IsCauseOf(Exception exception)
    {
        if (ReferenceEquals(exception, Failure)) { return true; }
        if (exception is AggregateException aggregate)
        { foreach (var inner in aggregate.InnerExceptions) { if (IsCauseOf(inner)) { return true; } } }
        return exception.InnerException is { } cause && IsCauseOf(cause);
    }

    internal void Disarm()
    {
        _armed = false;
        ObserveBatch(_coordinator.VersionStore, null);
    }

    protected override void OnStatementRolledBackCore()
    {
        Calls++;
        if (_armed && _stage == 2) { throw Failure; }
        Value = 7; // Repeated restoration after a callback fault must be idempotent.
        if (_armed && _stage == 3) { throw Failure; }
    }

    protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key,
        ulong reference, TransactionSequence writer, CancellationToken cancellationToken)
    {
        Calls++;
        if (_armed) { throw Failure; }
        return default;
    }

    protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key,
        ulong reference, TransactionSequence writer, CancellationToken cancellationToken)
        => EraseCoreAsync(transaction, key, reference, writer, cancellationToken);

    public void Dispose() => Disarm();
}

internal static class SavepointStreamCount
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BeginWriteStream")]
    internal static extern void Begin(TransactionContext context);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EndWriteStream")]
    internal static extern void End(TransactionContext context);
}

// A family-neutral test exchange; each model supplies only its existing codec.
internal sealed class SavepointWireControl
{
    private readonly ProtocolChannel _channel;
    private readonly ProtocolMessageType _execute;
    private readonly ProtocolMessageType _failure;
    private readonly Func<string, ReadOnlyMemory<byte>> _encode;
    private readonly Func<ProtocolFrame, string> _decodeFailure;
    private readonly CancellationToken _token;
    internal string? LastFailure;

    internal SavepointWireControl(ProtocolChannel channel, ProtocolMessageType execute, ProtocolMessageType failure,
        Func<string, ReadOnlyMemory<byte>> encode, Func<ProtocolFrame, string> decodeFailure, CancellationToken token)
    {
        _channel = channel;
        _execute = execute;
        _failure = failure;
        _encode = encode;
        _decodeFailure = decodeFailure;
        _token = token;
    }

    internal async Task StartAsync(string database)
    {
        await SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, database, "tester")
            { Capabilities = [ProtocolCapability.TransactionStatus.Name, ProtocolCapability.Diagnostics.Name] }.Encode());
        (await ReadAsync()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        var ready = await ReadAsync();
        ready.Type.ShouldBe(ProtocolMessageType.Ready);
        _channel.SetAgreement(ProtocolReadyMessage.Decode(ready.Payload.Span));
    }

    internal async Task<bool> ExecuteAsync(string statement)
    {
        LastFailure = null;
        await SendAsync(_execute, _encode(statement));
        while (true)
        {
            var frame = await ReadAsync();
            if (frame.Type == _failure) { LastFailure = _decodeFailure(frame); }
            if (frame.Type != ProtocolMessageType.Ready) { continue; }
            var status = ProtocolTransactionStatusMessage.Decode(frame.Payload.Span).Status;
            if (LastFailure is not null) { status.ShouldBe(ProtocolTransactionStatus.Failed); }
            else { status.ShouldNotBe(ProtocolTransactionStatus.Failed); }
            return LastFailure is null;
        }
    }

    private async Task SendAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
    {
        await _channel.Writer.WriteFrameAsync(new(type, payload), _token);
        await _channel.Writer.FlushAsync(_token);
    }

    private async Task<ProtocolFrame> ReadAsync()
        => (await _channel.Reader.ReadFrameAsync(_token)).ShouldNotBeNull();
}
