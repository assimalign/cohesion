using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Joint TYPE: a same-name compiled domain scale change rewrites its referring columns")]
    public async Task BuildAsync_DomainScaleChange_ShouldPublishConvertedValues()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SqlSchema Declare(int scale) => SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Type<Amount>(type => type.Decimal(8, scale));
            schema.Table<AmountRow>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); table.Index(row => row.Value); });
        });
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase(Declare(3)).BuildAsync(timeout.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,1.235)", cancellationToken: timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using var engine = await Builder(storage).AddDatabase(Declare(2)).BuildAsync(timeout.Token);
        var current = await engine.OpenDatabaseAsync("typed", timeout.Token);
        await WaitDoneAsync(current, timeout.Token);

        // Assert
        Table(current).FindColumn("Value")!.Type.Scale.ShouldBe(2);
        await using var reader = await current.CreateSessionAsync(timeout.Token);
        (await RowsAsync(reader, "SELECT Value FROM items", timeout.Token)).Single()[0].ShouldBe(1.24m);
        current.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(2).Compile().Hash);
    }

    private readonly record struct Amount(decimal Value);
    private sealed record AmountRow(int Id, Amount Value);
}
