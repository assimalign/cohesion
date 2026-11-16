using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed partial class DatabaseHostingEventSourceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Held schema: health and events name the code, hashes and refused steps and clear on convergence")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Health_HeldSchema_ShouldReportRefusalAndRestoration(bool previouslyApplied)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string path = Path.Combine(Path.GetTempPath(), "cohesion-health-rollback", Guid.NewGuid().ToString("N"));
        var first = HeldSchema(false);
        var second = HeldSchema(true);
        try
        {
            var seed = SqlDatabaseEngine.CreateBuilder("sql");
            seed.Options.RootPath = path;
            await using (var engine = await seed.BuildAsync(timeout.Token))
            {
                var database = await engine.CreateDatabaseAsync("held", timeout.Token);
                if (previouslyApplied) { await database.ApplySchemaAsync(first.Compile(), timeout.Token); }
                await database.ApplySchemaAsync(second.Compile(), timeout.Token);
            }
            var builder = DatabaseApplication.CreateBuilder();
            builder.AddSql("sql", sql => { sql.Options.RootPath = path; sql.AddDatabase(first); });
            using var recorder = new HostingEventRecorder(EventLevel.Informational);
            await using (var application = builder.Build(timeout.Token))
            {
                // Act
                var degraded = await application.Context.CheckAsync(timeout.Token);
                await application.Context.CheckAsync(timeout.Token);

                // Assert
                string code = previouslyApplied ? "CS03405" : "CS03105";
                degraded.Status.ShouldBe(HealthStatus.Degraded);
                degraded.Description!.ShouldContain("DropColumn 'items.Added'", Case.Sensitive);
                degraded.Description!.ShouldContain(first.Compile().Hash, Case.Sensitive);
                degraded.Description!.ShouldContain(second.Compile().Hash, Case.Sensitive);
                degraded.Data!["engine.0.provisioning.0.failureCode"].ShouldBe(code);
                degraded.Data["engine.0.provisioning.0.detail"].ShouldBeOfType<string>().ShouldContain("DropColumn 'items.Added'", Case.Sensitive);
                var entry = recorder.Events.Single(value => value.EventId == 16);
                entry.PayloadNames.ShouldBe(_healthPayload);
                entry.Payload![11].ShouldBe(code);
                entry.Payload[12].ShouldBeOfType<string>().ShouldContain("DropColumn 'items.Added'", Case.Sensitive);
                recorder.Events.ShouldNotContain(value => value.EventId == 17);
                recorder.Events.ShouldNotContain(value => value.EventId == 0);
            }
            var redeploy = DatabaseApplication.CreateBuilder();
            redeploy.AddSql("sql", sql => { sql.Options.RootPath = path; sql.AddDatabase(second); });
            await using var current = redeploy.Build(timeout.Token);
            (await current.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy);
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    private static SqlSchema HeldSchema(bool extra) => SqlSchema.Create("held", schema =>
        schema.Table<HeldRow>("items", table =>
        {
            table.Key(row => row.Id);
            if (extra) { table.Column(row => row.Added); }
        }));

    private sealed record HeldRow(int Id, int? Added);
}
