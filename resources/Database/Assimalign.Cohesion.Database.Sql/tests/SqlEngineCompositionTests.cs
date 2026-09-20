using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Verifies engine composition ownership and deferred lifecycle behavior.</summary>
public sealed class SqlEngineCompositionTests
{
    [Fact]
    public async Task Build_ShouldRunAndQuiesceAnInterfaceWorker()
    {
        var builder = SqlDatabaseEngine.CreateBuilder();
        var worker = new ProbeWorker();
        IDatabaseEngine? observed = null;
        builder.AddWorker(engine => { observed = engine; return worker; });
        observed.ShouldBeNull();

        var engine = builder.Build();
        observed.ShouldBeSameAs(engine);
        engine.Workers.ShouldContain(worker);
        worker.Started.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        await engine.DisposeAsync();
        worker.Stopped.ShouldBeTrue();
        worker.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Build_ShouldFreezeOptionsAndRejectASecondAttempt()
    {
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.EngineName = "frozen";
        await using var engine = builder.Build();
        Should.Throw<InvalidOperationException>(() => builder.EngineName = "changed");
        Should.Throw<InvalidOperationException>(() => builder.AddWorker(_ => new ProbeWorker()));
        Should.Throw<InvalidOperationException>(() => builder.Build());
        engine.Name.ShouldBe("frozen");
    }

    [Fact]
    public void FailedFactory_ShouldDisposeEngineAndForbidRetry()
    {
        var builder = SqlDatabaseEngine.CreateBuilder();
        IDatabaseEngine? created = null;
        builder.AddServer(engine => { created = engine; throw new InvalidOperationException("factory"); });
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldBe("factory");
        created.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public async Task WrongServerEngine_ShouldDisposeRejectedServerAndConstructedEngine()
    {
        await using var other = SqlDatabaseEngine.Create(new());
        var server = new ProbeServer(other);
        IDatabaseEngine? created = null;
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.AddServer(engine => { created = engine; return server; });
        Should.Throw<InvalidOperationException>(() => builder.Build());
        server.Disposals.ShouldBe(1);
        created.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        other.State.ShouldBe(EngineState.Running);
    }

    [Fact]
    public async Task DisposalFailure_ShouldStillDisposeEveryServerAndQuiesceWorkers()
    {
        var builder = SqlDatabaseEngine.CreateBuilder();
        var worker = new ProbeWorker();
        ProbeServer? first = null;
        builder.AddWorker(_ => worker);
        builder.AddServer(engine => first = new ProbeServer(engine));
        builder.AddServer(engine => new ProbeServer(engine) { FailDisposal = true });
        var engine = builder.Build();
        worker.Started.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        await Should.ThrowAsync<AggregateException>(async () => await engine.DisposeAsync());
        first.ShouldNotBeNull().Disposals.ShouldBe(1);
        worker.Stopped.ShouldBeTrue();
        worker.Disposed.ShouldBeTrue();
        await engine.DisposeAsync();
        first.Disposals.ShouldBe(1);
    }

    [Fact]
    public void RepeatedServerProduct_ShouldDisposeOnlyOnceDuringCompensation()
    {
        var builder = SqlDatabaseEngine.CreateBuilder();
        ProbeServer? server = null;
        builder.AddServer(engine => server = new ProbeServer(engine));
        builder.AddServer(_ => server!);
        Should.Throw<InvalidOperationException>(() => builder.Build());
        server.ShouldNotBeNull().Disposals.ShouldBe(1);
    }

    [Fact]
    public void CallbackBuildingEarly_ShouldCompensateItsUnreturnedEngine()
    {
        var application = new RecordingApplicationBuilder();
        IDatabaseEngine? early = null;
        application.AddSql((context, builder) => early = builder.Build());
        Should.Throw<InvalidOperationException>(() => application.MaterializeEngine());
        early.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact]
    public void SynchronousCleanup_ShouldAvoidTheCallersSynchronizationContext()
    {
        using var other = SqlDatabaseEngine.Create(new());
        var rejected = new ProbeServer(other) { YieldBeforeDisposal = true };
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.AddServer(_ => rejected);
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectingSynchronizationContext());
            Should.Throw<InvalidOperationException>(() => builder.Build());
            rejected.Disposals.ShouldBe(1);

            var valid = SqlDatabaseEngine.CreateBuilder();
            valid.AddServer(engine => new ProbeServer(engine) { YieldBeforeDisposal = true });
            valid.Build().Dispose();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallback_ShouldStillJoinWorkersBeforeDisposal()
    {
        var worker = new CancellationFailureWorker();
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.AddWorker(_ => worker);
        var engine = builder.Build();
        worker.Started.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        Task disposal = Task.Run(async () => await engine.DisposeAsync());
        try
        {
            worker.Cancelled.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            disposal.IsCompleted.ShouldBeFalse();
            worker.Disposed.ShouldBeFalse();
        }
        finally
        {
            worker.AllowStop.Set();
        }
        await Should.ThrowAsync<AggregateException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        worker.Disposed.ShouldBeTrue();
        worker.Stopped.ShouldBeTrue();
    }

    private sealed class ProbeWorker : IDatabaseEngineWorker, IDisposable
    {
        public string Name => "interface-worker";
        public DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.Checkpoint;
        public TimeSpan Interval => TimeSpan.FromMilliseconds(1);
        public ManualResetEventSlim Started { get; } = new();
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public void Run(CancellationToken cancellationToken)
        {
            Started.Set();
            cancellationToken.WaitHandle.WaitOne();
            Stopped = true;
        }
        public void Dispose() { Disposed = true; Started.Dispose(); }
    }

    private sealed class ProbeServer(IDatabaseEngine engine) : IDatabaseServer
    {
        public IDatabaseServerContext Context { get; } = new ProbeContext(engine);
        public bool FailDisposal { get; init; }
        public bool YieldBeforeDisposal { get; init; }
        public int Disposals { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            if (YieldBeforeDisposal)
            {
                await Task.Yield();
            }
            Disposals++;
            if (FailDisposal)
            {
                throw new InvalidOperationException("server disposal");
            }
        }
    }

    private sealed class ProbeContext(IDatabaseEngine engine) : IDatabaseServerContext
    {
        public IDatabaseEngine Engine => engine;
        public IReadOnlyCollection<IDatabaseServerSession> Sessions => [];
    }

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => throw new InvalidOperationException("Cleanup captured the caller's synchronization context.");
    }

    private sealed class CancellationFailureWorker : IDatabaseEngineWorker, IDisposable
    {
        public string Name => "cancellation-failure";
        public DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.Checkpoint;
        public TimeSpan Interval => TimeSpan.FromMilliseconds(1);
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Cancelled { get; } = new();
        public ManualResetEventSlim AllowStop { get; } = new();
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public void Run(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                Cancelled.Set();
                throw new InvalidOperationException("custom cancellation callback failed");
            });
            Started.Set();
            cancellationToken.WaitHandle.WaitOne();
            AllowStop.Wait();
            Stopped = true;
        }

        public void Dispose()
        {
            Stopped.ShouldBeTrue();
            Disposed = true;
            Started.Dispose();
            Cancelled.Dispose();
            AllowStop.Dispose();
        }
    }
}
