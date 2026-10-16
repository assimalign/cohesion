using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDeferredProvisioningTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CHAR widening validates on the provisioning worker and refuses repadding")]
    public async Task BuildAsync_CharacterWidening_ShouldDeferValidationAndRejectRewrite()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema CharacterSchema(int length) => SqlSchema.Create("pending", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<CharacterRow>("chars", table => table.Column(row => row.Value, $"CHAR({length})"));
        });
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("pending", timeout.Token);
            await database.ApplySchemaAsync(CharacterSchema(2).Compile(), timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync("INSERT INTO chars VALUES ('a')", cancellationToken: timeout.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        int validations = 0;
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
            {
                Interlocked.Increment(ref validations);
                release.Wait(token);
            };
            return new RecordingWorker(engine);
        });
        builder.AddDatabase(CharacterSchema(4));

        // Act
        await using var engine = await builder.BuildAsync(timeout.Token);
        var target = await engine.OpenDatabaseAsync("pending", timeout.Token);
        try
        {
            target.Catalog.PendingMigration.ShouldNotBeNull().Steps.ShouldHaveSingleItem().Kind.ShouldBe("AlterColumn");
        }
        finally
        {
            release.Set();
        }
        while (target.OfflineError is null) { await Task.Delay(1, timeout.Token); }

        // Assert
        Volatile.Read(ref validations).ShouldBe(1);
        target.OfflineError.Message.ShouldContain("would rewrite existing values");
        target.Catalog.TryGetTable("dbo", "chars", out var table).ShouldBeTrue();
        table.FindColumn("Value").ShouldNotBeNull().Type.MaxLength.ShouldBe(2);
    }

    private sealed record CharacterRow(string? Value);
}
