using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tests for the SQL model's application-builder verb: <c>AddSqlDatabase</c>
/// registers a configured engine against the area root's
/// <c>IDatabaseApplicationBuilder</c> seam without any hosting-layer knowledge.
/// </summary>
public sealed class SqlDatabaseApplicationBuilderTests : IDisposable
{
    private readonly string _rootPath;

    public SqlDatabaseApplicationBuilderTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-builder", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - AddSqlDatabase: Registers a configured engine on the root builder seam")]
    public void AddSqlDatabase_WithOptions_ShouldRegisterConfiguredEngine()
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();

        // Act
        SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
        {
            options.EngineName = "verb-engine";
            options.RootPath = _rootPath;
            options.Durability = StorageCommitDurability.Grouped;
        });

        // Assert: the verb registered exactly the engine it returned.
        builder.Engines.ShouldHaveSingleItem();
        builder.Engines[0].ShouldBeSameAs(engine);
        engine.Name.ShouldBe("verb-engine");
        engine.Model.ShouldBe(EngineModel.Sql);
        engine.State.ShouldBe(EngineState.Idle);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - AddSqlDatabase: Defaults register an in-memory engine that serves SQL")]
    public async Task AddSqlDatabase_WithDefaults_ShouldServeSqlEndToEnd()
    {
        // Arrange: no root path — the in-memory strategy.
        var builder = new RecordingApplicationBuilder();
        await using SqlDatabaseEngine engine = builder.AddSqlDatabase();

        // Act: drive the registered engine end-to-end through the root contracts.
        await engine.StartAsync(TestContextToken());
        IDatabase database = await engine.CreateDatabaseAsync("builder-db", TestContextToken());
        await using IDatabaseSession session = await database.CreateSessionAsync(TestContextToken());

        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, label VARCHAR(50));", cancellationToken: TestContextToken());
        await session.ExecuteAsync("INSERT INTO items (id, label) VALUES (1, 'one'), (2, 'two');", cancellationToken: TestContextToken());
        QueryResult result = await session.ExecuteAsync("SELECT label FROM items ORDER BY id;", cancellationToken: TestContextToken());

        // Assert
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();
        var labels = new List<object?>();

        await foreach (QueryRow row in resultSet!.GetRowsAsync(TestContextToken()))
        {
            labels.Add(row.GetValue(0));
        }

        labels.ShouldBe(["one", "two"]);
    }

    private static CancellationToken TestContextToken()
        => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
