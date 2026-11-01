using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Portable planning: destructive policy refusal carries the provisioning identity")]
    public async Task ApplyAsync_PortablePolicyRefusal_ShouldReportCodedFailure()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var proposed = Source().Compile();
        var target = new SqlCompiledSchema(proposed.Format, proposed.Name, false, proposed.Types,
            proposed.Tables, proposed.Principals);
        // Changing the destructive opt-in alone is metadata-only; a dropped populated
        // table makes the planner exercise the permanent policy refusal instead.
        target = new SqlCompiledSchema(target.Format, target.Name, false, target.Types, [], target.Principals);

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, guard.Token));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03105");
        database.Provisioning!.FailureCode.ShouldBe("CS03105");
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(proposed.Hash);
        database.OfflineError.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint reservation: timeout throws the same code as provisioning")]
    public async Task ApplyAsync_ReservationTimeout_ShouldAgreeWithProvisioningCode()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var builder = Builder(await SeedAsync(guard.Token));
        builder.Options.LockTimeout = TimeSpan.FromMilliseconds(80);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        await using var holder = await database.CreateSessionAsync(guard.Token);
        await using var transaction = await holder.BeginTransactionAsync(guard.Token);
        await holder.ExecuteAsync("UPDATE items SET Value=Value WHERE Id=1", cancellationToken: guard.Token);
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(Target().Compile(), guard.Token));
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == database.Provisioning!.FailureCode);
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        database.Catalog.PendingMigration.ShouldBeNull();
        database.OfflineError.ShouldBeNull();
        await transaction.RollbackAsync(guard.Token);
        await database.ApplySchemaAsync(Target().Compile(), guard.Token);
    }
}
