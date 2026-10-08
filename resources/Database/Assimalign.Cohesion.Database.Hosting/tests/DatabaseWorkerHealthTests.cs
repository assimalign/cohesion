using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// The application's health while an engine's workers fail and its databases go offline (#1268):
/// a worker that keeps failing makes it degraded and is named with its failure, a worker that
/// completes a pass again makes it healthy, and an offline database makes it unhealthy until the
/// database is reopened.
/// </summary>
/// <remarks>
/// Until phase 4 of the concrete-types plan these tests failed a real SQL engine's checkpointer
/// and header writes through the SQL model's fault-injecting storage strategy, internal since then
/// (D9). The worker half still runs on a real SQL engine: a guided worker registered through the
/// typed <see cref="SqlDatabaseEngineBuilder.AddWorker"/> fails a pass the test runs, and the root
/// engine base folds the engine's state from it. The offline half here drives the engine double,
/// a leaf of the root engine base that reports the offline databases the test sets. A real
/// engine's database goes offline in <c>DatabaseReopenTests</c>: since owner decisions 25 and 42
/// such a registered worker takes it offline once its failures have lasted the engine's
/// <see cref="DatabaseEngine.WorkerFailureWindow"/> across its
/// <see cref="DatabaseEngine.WorkerFailureMinimumPasses"/> failed passes.
/// </remarks>
public sealed class DatabaseWorkerHealthTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a failing worker of a real engine is degraded and named, and healthy once it recovers")]
    public async Task CheckAsync_WorkerFaultsOnARealEngine_ShouldFollowTheWorker()
    {
        // Arrange: a SQL engine with a guided worker the test drives pass by pass, beside its
        // built-in workers.
        ReportingWorker? registered = null;
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.EngineName = "sql";
        builder.AddWorker(_ => registered = new ReportingWorker("sql/probe", DatabaseEngineWorkerKind.Checkpoint));
        await using var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);
        HealthContribution before = await application.Context.CheckAsync(CancellationToken.None);

        // Act: the worker's pass fails for the database, and the engine base folds it as Faulted.
        worker.Failure = new IOException("Injected page write failure");
        bool failed = !worker.RunIteration(CancellationToken.None);
        var faulted = engine.State;
        HealthContribution degraded = await application.Context.CheckAsync(CancellationToken.None);

        worker.Failure = null;
        Thread.Sleep(DatabaseEngineWorker.FailureBackoff);
        bool recovered = worker.RunIteration(CancellationToken.None) && worker.Fault is null;
        var running = engine.State;
        HealthContribution healthy = await application.Context.CheckAsync(CancellationToken.None);

        // Assert: degraded names the worker and the type of its failure, and the data carries it;
        // the failure's message, which can carry file paths, stays out of the unauthenticated
        // health output.
        engine.Workers.ShouldContain(worker);
        before.Status.ShouldBe(HealthStatus.Healthy);
        failed.ShouldBeTrue();
        faulted.ShouldBe(EngineState.Faulted);
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldNotBeNull().ShouldContain("sql/probe");
        degraded.Description.ShouldContain("IOException");
        degraded.Description.ShouldContain("returns to Running");
        degraded.Description.ShouldNotContain("Injected page write failure");
        IReadOnlyDictionary<string, object> data = degraded.Data.ShouldNotBeNull();
        string prefix = data.Single(entry => entry.Value is "sql/probe").Key[..^".name".Length];
        data[$"{prefix}.fault"].ShouldBeOfType<string>().ShouldContain("IOException");
        data.Values.OfType<string>().ShouldNotContain(value => value.Contains("Injected page write failure", StringComparison.Ordinal));
        ((int)data[$"{prefix}.consecutiveFailures"]).ShouldBeGreaterThanOrEqualTo(1);
        ((long)data[$"{prefix}.failureCount"]).ShouldBeGreaterThanOrEqualTo(1);

        recovered.ShouldBeTrue();
        running.ShouldBe(EngineState.Running);
        healthy.Status.ShouldBe(HealthStatus.Healthy);
        healthy.Data.ShouldNotBeNull().ContainsKey($"{prefix}.fault").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: an offline database is unhealthy until the reopen, while its engine runs")]
    public async Task CheckAsync_OfflineDatabase_ShouldBeUnhealthyUntilTheReopen()
    {
        // Arrange: a running engine whose database the test takes offline, as a failed file
        // header write does.
        var engine = new RecordingEngine("sql");
        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);

        // Act
        engine.Offline = ["app"];
        HealthContribution unhealthy = await application.Context.CheckAsync(CancellationToken.None);

        engine.Offline = [];
        HealthContribution reopened = await application.Context.CheckAsync(CancellationToken.None);

        // Assert
        unhealthy.Status.ShouldBe(HealthStatus.Unhealthy);
        unhealthy.Description.ShouldNotBeNull().ShouldContain("Offline databases: sql/app.");
        unhealthy.Description.ShouldContain("every operation on them is refused until each is reopened");
        unhealthy.Data.ShouldNotBeNull()["engine.0.state"].ShouldBe(nameof(EngineState.Running));
        unhealthy.Data["engine.0.offline.0.name"].ShouldBe("app");

        // The double holds no storage, so its engine reports no storage error and health names
        // no cause; a real engine's cause is read in DatabaseReopenTests.
        unhealthy.Data.ContainsKey("engine.0.offline.0.cause").ShouldBeFalse();

        reopened.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// A worker failing on one database of a hosted Blob engine (owner decision 42 of 2026-10-07):
    /// health is Degraded and names the failing worker, as for any failing worker, while the
    /// engine's Blob server, which the application starts, serves the engine's healthy database
    /// over the wire and refuses only the failing one, with <c>COHDBB003</c>. Before the decision
    /// the server refused every database while its engine was Faulted.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Context health: a Blob worker failing on one database is degraded and named while the server serves the other databases")]
    public async Task CheckAsync_BlobWorkerFailingOnOneDatabase_ShouldBeDegradedWhileTheServerServesTheOthers()
    {
        // Arrange: a Blob engine with a nested server and a guided worker that fails database
        // "failing" while its failure is set; the engine's give-up is out of reach.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var listener = new InMemoryConnectionListener();
        ReportingWorker? registered = null;
        var builder = BlobDatabaseEngine.CreateBuilder();
        builder.EngineName = "blob";
        builder.WorkerFailureMinimumPasses = int.MaxValue;
        builder.AddWorker(_ => registered = new ReportingWorker("blob/probe", DatabaseEngineWorkerKind.Checkpoint, "failing"));
        builder.AddServer(built => BlobDatabaseServer.Create(built, new() { Listener = listener }));
        await using var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        await engine.CreateDatabaseAsync("failing", token);
        var healthy = await engine.CreateDatabaseAsync("healthy", token);
        await using (var session = await healthy.CreateSessionAsync(token))
        {
            await session.CreateContainerAsync("files", token);
        }

        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);

        // Act: the worker fails on "failing"; the application starts the engine's server.
        worker.Failure = new IOException("Injected page write failure");
        worker.RunIteration(token);
        await ((IHost)application).StartAsync(token);
        HealthContribution degraded = await application.Context.CheckAsync(token);
        var served = await HandshakeAsync(listener, "healthy", listContainer: true, token);
        var refused = await HandshakeAsync(listener, "failing", listContainer: false, token);
        await ((IHost)application).StopAsync(token);

        // Assert
        engine.State.ShouldBe(EngineState.Faulted);
        engine.HasFailingWorker("failing").ShouldBeTrue();
        engine.HasFailingWorker("healthy").ShouldBeFalse();
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldNotBeNull().ShouldContain("blob/probe");
        degraded.Description.ShouldContain("IOException");
        served.ShouldBe((ProtocolMessageType.Ready, (ProtocolErrorCode?)null, (string?)null, (long?)0));
        refused.Type.ShouldBe(ProtocolMessageType.Error);
        refused.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        refused.Message.ShouldNotBeNull().ShouldStartWith("COHDBB003", Case.Sensitive);
        refused.Listed.ShouldBeNull();
    }

    // Starts a session of the database on the Blob server behind the listener, and lists its
    // container "files" once it is ready: the frame that ends the handshake, the error it carries,
    // and the count the listing completed with.
    private static async Task<(ProtocolMessageType Type, ProtocolErrorCode? Code, string? Message, long? Listed)> HandshakeAsync(
        InMemoryConnectionListener listener, string database, bool listContainer, CancellationToken token)
    {
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, database, "tester").Encode()), token);
        await channel.Writer.FlushAsync(token);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty), token);
        await channel.Writer.FlushAsync(token);
        var ended = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        if (ended.Type == ProtocolMessageType.Error)
        {
            var error = ProtocolErrorMessage.Decode(ended.Payload.Span);
            return (ended.Type, error.Code, error.Message, null);
        }

        long? listed = null;
        if (listContainer)
        {
            await channel.Writer.WriteFrameAsync(new((ProtocolMessageType)BlobProtocolMessageType.List, new BlobListMessage("files", "").Encode()), token);
            await channel.Writer.FlushAsync(token);
            var complete = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
            listed = BlobOperationCompleteMessage.Decode(complete.Payload.Span).Count;
        }

        return (ended.Type, null, null, listed);
    }

    /// <summary>
    /// A guided worker whose pass the test runs: each pass reports <see cref="Failure"/> for its
    /// database (<c>app</c> unless given) while it is set, and finishes that database's work
    /// otherwise. Its one-hour interval keeps the engine's pump from running a pass the test did
    /// not ask for after the first.
    /// </summary>
    private sealed class ReportingWorker : DatabaseEngineWorker
    {
        private readonly string _database;

        public ReportingWorker(string name, DatabaseEngineWorkerKind kind, string database = "app")
            : base(name, kind, TimeSpan.FromHours(1))
        {
            _database = database;
        }

        public Exception? Failure { get; set; }

        protected override void RunIterationCore(CancellationToken cancellationToken)
        {
            if (!BeginDatabase(_database))
            {
                return;
            }

            if (Failure is { } failure)
            {
                ReportFailure(_database, failure);
            }
        }
    }
}
