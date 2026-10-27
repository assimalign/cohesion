using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlViewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Views: changed scalar native output never declares numeric collation or blocks reopen")]
    [InlineData("SELECT (SELECT native_value(id) FROM t) AS y FROM t UNION ALL SELECT (SELECT native_value(id) FROM t) FROM t", 2)]
    [InlineData("SELECT CASE WHEN id = 1 THEN (SELECT native_value(id) FROM t) ELSE (SELECT native_value(id) FROM t) END AS y FROM t", 1)]
    [InlineData("SELECT COALESCE((SELECT native_value(id) FROM t), (SELECT native_value(id) FROM t)) AS y FROM t", 1)]
    public async Task Open_ChangedScalarNativeOutput_ShouldDeferCodedTypeFailure(string body, int count)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var initial = SqlDatabaseEngine.CreateBuilder("view-scalar-native");
        initial.Options.StorageStrategy = storage;
        initial.Functions.Add(SqlScalarFunction.Create("native_value", static (int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await using (var engine = await initial.BuildAsync(CancellationToken.None))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE t (id INT)");
            await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
            await ExecuteAsync(session, "CREATE VIEW v AS " + body);
            (await RowsAsync(session, "SELECT * FROM v")).Count.ShouldBe(count);
        }
        var changed = SqlDatabaseEngine.CreateBuilder("view-scalar-native");
        changed.Options.StorageStrategy = storage.CaptureDurableImages();
        changed.Functions.Add(SqlScalarFunction.Create("native_value", static (int value) => value));

        // Act
        await using var reopened = await changed.BuildAsync(CancellationToken.None);
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(reader, "SELECT * FROM v"));
        var metadata = await RowsAsync(reader, "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");
        var rows = await RowsAsync(reader, "SELECT COUNT(*) FROM t");
        await ExecuteAsync(reader, "DROP VIEW v");

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CS01009");
        error.Message.ShouldBe("View 'dbo.v' cannot be bound. A source definition changed its output type, modifiers, domain or collation.");
        error.InnerException.ShouldBeOfType<SqlPlanningException>();
        metadata.ShouldHaveSingleItem().ShouldBe(["CHARACTER VARYING"]);
        rows.ShouldHaveSingleItem().ShouldBe([1L]);
        restored.Catalog.Views.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: unexpected recoverable binder failures stay view-local and preserve their cause")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindViews_UnexpectedFailure_ShouldDeferWithOriginalCause(bool argument)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-binding-fault", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        var planner = new SqlPlanner(database.Catalog, null, database.Definitions.Functions);
        Exception failure = argument ? new ArgumentException("binding fault") : new InvalidOperationException("binding fault");
        SqlViewBindingFault.Inject(planner, database.Catalog.Views.Single(), failure);

        // Act
        planner.BindViews();
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT * FROM v"));
        var rows = await RowsAsync(session, "SELECT COUNT(*) FROM t");
        await ExecuteAsync(session, "DROP VIEW v");

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CS03033");
        error.InnerException.ShouldBeSameAs(failure);
        rows.ShouldHaveSingleItem().ShouldBe([3L]);
        database.Catalog.Views.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: engine failures propagate during open and read binding")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task BindViews_FatalFailure_ShouldPropagate(int kind)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-fatal-binding", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        var planner = new SqlPlanner(database.Catalog, null, database.Definitions.Functions);
        var offline = await engine.CreateDatabaseAsync("offline", CancellationToken.None);
        offline.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "binding offline", new IOException("device failure")).ShouldBeTrue();
        Exception failure = kind switch
        {
            0 => new OperationCanceledException(),
            1 => new OutOfMemoryException(),
            2 => new StorageCorruptionException("corrupt"),
            3 => new StorageIOException("I/O"),
            4 => new IOException("I/O"),
            6 => new InsufficientExecutionStackException(),
            7 => offline.DataStorage.OfflineError!,
            _ => new AggregateException(new InvalidOperationException("wrapped I/O", new IOException("I/O"))),
        };
        SqlViewBindingFault.Inject(planner, database.Catalog.Views.Single(), failure);

        // Act
        var error = Should.Throw<Exception>(() => planner.BindViews());
        var query = (Language.SqlQueryStatement)new Language.SqlQueryParser().Parse("SELECT * FROM v");
        var readError = Should.Throw<Exception>(() => planner.Plan(query.SqlExpression!));

        // Assert
        error.ShouldBeSameAs(failure);
        readError.ShouldBeSameAs(failure);
    }
}
