using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

[Collection(DatabaseHealthCollection.Name)]
public sealed class DatabaseFailedOpenHealthTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Every model reports failed stored opens until repair or drop (#1790)")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task GetHealthAsync_WithStoredOpenFailure_ShouldReportUntilRepairOrDrop(int model, bool enumerate)
    {
        // Arrange: the exclusive data handle injects a real stored-file I/O refusal.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TemporaryDirectory();
        await using var engine = CreateEngine(model, directory.Path);
        await (await engine.CreateDatabaseAsync("Stored", guard.Token)).DisposeAsync();
        string dataFile = Directory.EnumerateFiles(directory.Path, "*.dat", SearchOption.AllDirectories).First();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        builder.AddHealthEndpoint(endpoint);
        await using var application = builder.Build(guard.Token);
        reservation.Stop();
        await ((IHost)application).StartAsync(guard.Token);
        using var client = new HttpClient { BaseAddress = endpoint };

        // Act
        DatabaseException thrown;
        using (var locked = new FileStream(dataFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            thrown = await Should.ThrowAsync<DatabaseException>(async () =>
            {
                if (enumerate) { await foreach (var database in engine.GetDatabasesAsync(guard.Token)) { } }
                else { await engine.OpenDatabaseAsync("stored", guard.Token); }
            });
        }
        var component = await application.Context.GetHealthAsync("database/model%2Fengine/stored", guard.Token);
        var components = await application.Context.GetHealthAsync(guard.Token);
        var resource = new Internal.DatabaseResource("health", null, [], application.Context);
        var contributor = resource.HealthContributors.Single(value => value.Name == component.Name);
        var contribution = await contributor.CheckAsync(guard.Token);
        using var ready = await client.GetAsync("/readyz", guard.Token);
        using var live = await client.GetAsync("/livez", guard.Token);

        // Assert
        var record = engine.FailedOpens.ShouldHaveSingleItem();
        thrown.Diagnostics.ShouldNotBeEmpty();
        record.FailureCode.ShouldBe(thrown.Diagnostics[0].Code);
        record.ExceptionType.ShouldBe(thrown.GetType().Name);
        record.Attempts.ShouldBe(1);
        component.Status.ShouldBe(DatabaseHealthStatus.Unhealthy);
        component.Description.ShouldNotBeNull().ShouldContain("open failed");
        component.Data["failureCode"].ShouldBe(record.FailureCode);
        component.Data["reopenFault"].ShouldBe(record.ExceptionType);
        component.Data["reopenAttempts"].ShouldBe("1");
        component.Data["failedAt"].ShouldBe(record.FailedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        application.Context.HealthComponentNames.ShouldBe(["engine/model%2Fengine", "database/model%2Fengine/stored", "database.accepting"]);
        components.Select(value => value.Name).ShouldBe(application.Context.HealthComponentNames);
        resource.HealthContributors.Select(value => value.Name).ShouldBe(application.Context.HealthComponentNames);
        contribution.Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
        contribution.Data.ShouldBe(component.Data);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ready.Content.ReadAsStringAsync(guard.Token)).ShouldNotContain(thrown.Message, Case.Sensitive);

        // Repair is releasing the exclusive handle; successful open ends this failure episode.
        await engine.OpenDatabaseAsync("stored", guard.Token);
        engine.FailedOpens.ShouldBeEmpty();
        (await application.Context.GetHealthAsync(component.Name, guard.Token)).Status.ShouldBe(DatabaseHealthStatus.Healthy);
        (await contributor.CheckAsync(guard.Token)).Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
        using var repaired = await client.GetAsync("/readyz", guard.Token);
        repaired.StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await engine.OpenDatabaseAsync("stored", guard.Token)).DisposeAsync();
        using (var locked = new FileStream(dataFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("stored", guard.Token)); }
        engine.FailedOpens.ShouldHaveSingleItem().Attempts.ShouldBe(1);
        await engine.DropDatabaseAsync("stored", guard.Token);
        engine.FailedOpens.ShouldBeEmpty();
        application.Context.HealthComponentNames.ShouldNotContain(component.Name);
        resource.HealthContributors.ShouldNotContain(value => value.Name == component.Name);
        await ((IHost)application).StopAsync(guard.Token);
    }

    private static DatabaseEngine CreateEngine(int model, string path)
    {
        const string name = "model/engine";
        if (model == 0) { var builder = SqlDatabaseEngine.CreateBuilder(name); builder.Options.RootPath = path; return builder.Build(); }
        if (model == 1) { var builder = KeyValueDatabaseEngine.CreateBuilder(name); builder.Options.RootPath = path; return builder.Build(); }
        if (model == 2) { var builder = DocumentDatabaseEngine.CreateBuilder(name); builder.Options.RootPath = path; return builder.Build(); }
        if (model == 3) { var builder = GraphDatabaseEngine.CreateBuilder(name); builder.Options.RootPath = path; return builder.Build(); }
        var blob = BlobDatabaseEngine.CreateBuilder(name); blob.Options.RootPath = path; return blob.Build();
    }
}
