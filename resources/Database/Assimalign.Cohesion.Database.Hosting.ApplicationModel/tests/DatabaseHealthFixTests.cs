using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

[Collection(DatabaseHealthCollection.Name)]
public sealed class DatabaseHealthFixTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Health never waits for the KeyValue recovery monitor (#1790)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CheckAsync_WithRegistryMonitorHeld_ShouldReadWithoutWaiting(int path)
    {
        // Arrange: open/recovery holds this exact monitor; keep it held until the read completes.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = KeyValueDatabaseEngine.CreateBuilder("kv").Build();
        await engine.CreateDatabaseAsync("open", guard.Token);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        var holder = Task.Factory.StartNew(() =>
        {
            lock (RegistryMonitor(engine)) { entered.SetResult(); release.Wait(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(guard.Token);

        // Act
        var read = Task.Run(async () =>
        {
            if (path == 0) { await CheckContextAsync(application.Context, guard.Token); }
            else if (path == 1) { await application.Context.GetHealthAsync(guard.Token); }
            else { _ = new Internal.DatabaseResource("health", null, [], application.Context).HealthContributors; }
        }, guard.Token);
        try { await read.WaitAsync(guard.Token); }
        finally
        {
            release.Set();
            await holder;
            try { await read; } catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
        }

        // Assert
        engine.TryGetDatabase("open", out _).ShouldBeTrue();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_syncRoot")]
    private static extern ref object RegistryMonitor(KeyValueDatabaseEngine engine);

    [Fact(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Health preserves an offline Documents instance and its cause (#1790)")]
    public async Task GetHealthAsync_WithOfflineDocuments_ShouldNeverReopen()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var worker = new HealthFailureWorker();
        var documents = DocumentDatabaseEngine.CreateBuilder("docs");
        documents.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        documents.Options.WorkerFailureMinimumPasses = 2;
        documents.AddWorker(_ => worker);
        await using var engine = documents.Build();
        var database = await engine.CreateDatabaseAsync("offline", guard.Token);
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        worker.RunIteration(guard.Token);
        worker.RunIteration(guard.Token);
        while (engine.GetOfflineError("offline") is null) { await Task.Delay(1, guard.Token); }

        // Act
        var first = await application.Context.GetHealthAsync(guard.Token);
        var second = await application.Context.GetHealthAsync(guard.Token);

        // Assert
        foreach (var observations in new[] { first, second })
        {
            var offline = observations.Single(value => value.Name.StartsWith("database/", StringComparison.Ordinal));
            offline.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
            offline.Data["cause"].ShouldBe("CheckpointFailures");
        }
        engine.TryGetDatabase("offline", out var retained).ShouldBeTrue();
        retained.ShouldBeSameAs(database);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Health never opens stored databases in any enumeration model (#1790)")]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task CheckAsync_WithStoredUnopenedDatabase_ShouldLeaveItClosed(int model, int path)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryDirectory();
        await using (var seed = CreateEngine(model, directory.Path)) { await seed.CreateDatabaseAsync("stored", guard.Token); }
        await using var engine = CreateEngine(model, directory.Path);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);

        // Act
        if (path == 0) { await CheckContextAsync(application.Context, guard.Token); }
        else if (path == 1) { await application.Context.GetHealthAsync(guard.Token); }
        else
        {
            var resource = new Internal.DatabaseResource("health", null, [], application.Context);
            _ = resource.HealthContributors;
        }

        // Assert
        engine.TryGetDatabase("stored", out _).ShouldBeFalse();
    }

    private static DatabaseEngine CreateEngine(int model, string path)
    {
        if (model == 0) { var builder = DocumentDatabaseEngine.CreateBuilder("docs"); builder.Options.RootPath = path; return builder.Build(); }
        if (model == 1) { var builder = GraphDatabaseEngine.CreateBuilder("graph"); builder.Options.RootPath = path; return builder.Build(); }
        var blob = BlobDatabaseEngine.CreateBuilder("blob"); blob.Options.RootPath = path; return blob.Build();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CheckAsync")]
    private static extern ValueTask<HealthCheckResult> CheckContextAsync(DatabaseApplicationContext context, CancellationToken cancellationToken);

    [Fact(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Build ignores undeclared stored databases and interrupted creates (#1790)")]
    public async Task Build_WithUndeclaredDocuments_ShouldLeaveStoredDatabasesClosed()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryDirectory();
        var seed = DocumentDatabaseEngine.CreateBuilder("seed");
        seed.Options.RootPath = directory.Path;
        await using (var engine = seed.Build()) { await engine.CreateDatabaseAsync("undeclared", guard.Token); }
        Directory.CreateDirectory(Path.Combine(directory.Path, "interrupted"));
        File.WriteAllBytes(Path.Combine(directory.Path, "interrupted", "document.dat"), []);
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddDocuments("docs", documents => documents.Options.RootPath = directory.Path);
        DatabaseResourceHealthTests.Register(builder);

        // Act
        await using var application = builder.Build(guard.Token);

        // Assert
        application.Context.GetEngine("docs").TryGetDatabase("undeclared", out _).ShouldBeFalse();
    }
}
