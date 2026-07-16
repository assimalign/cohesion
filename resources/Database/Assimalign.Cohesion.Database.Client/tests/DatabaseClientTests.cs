using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// End-to-end tests for the shared client core (#852): connect + handshake,
/// execute with typed results, error mapping, and session-reusing pooling —
/// against the real server and a live SQL engine over the in-memory driver.
/// </summary>
public class DatabaseClientTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Execute: SELECT materializes typed columns and rows over the wire")]
    public async Task ExecuteAsync_Select_ShouldMaterializeTypedRows()
    {
        // Arrange
        await using var harness = await ClientTestHarness.StartAsync();
        await using var connection = await harness.Client.RentAsync(ClientTestHarness.Timeout());

        // Act
        var result = await connection.ExecuteAsync("SELECT id, name FROM users ORDER BY id", cancellationToken: ClientTestHarness.Timeout());

        // Assert
        result.AffectedCount.ShouldBe(-1);
        result.Columns.Count.ShouldBe(2);
        result.Columns[0].ShouldBe(new DatabaseClientColumn("id", DatabaseType.Int32));
        result.Columns[1].ShouldBe(new DatabaseClientColumn("name", DatabaseType.String));

        result.Rows.Count.ShouldBe(2);
        result.Rows[0].ShouldBe([1, "ada"]);
        result.Rows[1].ShouldBe([2, "grace"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Execute: parameters round-trip through the shared value codec")]
    public async Task ExecuteAsync_WithParameters_ShouldBindValues()
    {
        // Arrange
        await using var harness = await ClientTestHarness.StartAsync();
        await using var connection = await harness.Client.RentAsync(ClientTestHarness.Timeout());

        // Act
        var result = await connection.ExecuteAsync(
            "SELECT name FROM users WHERE id = @id",
            new Dictionary<string, object?> { ["id"] = 2 },
            ClientTestHarness.Timeout());

        // Assert
        var row = result.Rows.ShouldHaveSingleItem();
        row.ShouldBe(["grace"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Execute: statement results carry the affected count")]
    public async Task ExecuteAsync_Insert_ShouldReturnAffectedCount()
    {
        // Arrange
        await using var harness = await ClientTestHarness.StartAsync();
        await using var connection = await harness.Client.RentAsync(ClientTestHarness.Timeout());

        // Act
        var result = await connection.ExecuteAsync("INSERT INTO users (id, name) VALUES (3, 'lin')", cancellationToken: ClientTestHarness.Timeout());

        // Assert
        result.AffectedCount.ShouldBe(1);
        result.Columns.ShouldBeEmpty();
        result.Rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Errors: server error frames surface as client exceptions with the wire code")]
    public async Task ExecuteAsync_InvalidStatement_ShouldThrowWithWireCode_AndConnectionSurvives()
    {
        // Arrange
        await using var harness = await ClientTestHarness.StartAsync();
        await using var connection = await harness.Client.RentAsync(ClientTestHarness.Timeout());

        // Act: an unknown command → ParseFailure; a missing table → ExecutionFailure
        var parseFailure = await Should.ThrowAsync<DatabaseClientException>(
            async () => await connection.ExecuteAsync("TRUNCATE TABLE users;", cancellationToken: ClientTestHarness.Timeout()));

        var executionFailure = await Should.ThrowAsync<DatabaseClientException>(
            async () => await connection.ExecuteAsync("SELECT id FROM missing_table", cancellationToken: ClientTestHarness.Timeout()));

        // Assert: codes map, and the connection is still usable afterwards
        parseFailure.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        executionFailure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        connection.IsOpen.ShouldBeTrue();

        var result = await connection.ExecuteAsync("SELECT id FROM users WHERE id = 1", cancellationToken: ClientTestHarness.Timeout());
        result.Rows.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Pooling: returning and re-renting reuses the authenticated session")]
    public async Task RentAsync_AfterReturn_ShouldReuseAuthenticatedSession()
    {
        // Arrange
        await using var harness = await ClientTestHarness.StartAsync();

        // Act: rent, execute, return; then rent again
        var first = await harness.Client.RentAsync(ClientTestHarness.Timeout());
        await first.ExecuteAsync("SELECT id FROM users WHERE id = 1", cancellationToken: ClientTestHarness.Timeout());
        await first.DisposeAsync();

        var second = await harness.Client.RentAsync(ClientTestHarness.Timeout());
        await second.ExecuteAsync("SELECT id FROM users WHERE id = 2", cancellationToken: ClientTestHarness.Timeout());

        // Assert: the same client connection (and its server session) served both
        // rents — no re-dial, no re-authentication.
        second.ShouldBeSameAs(first);
        harness.Server.Context.Sessions.ShouldHaveSingleItem();

        await second.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Pooling: an exhausted pool waits for a return")]
    public async Task RentAsync_AtPoolLimit_ShouldWaitForReturn()
    {
        // Arrange
        await using var harness = await ClientTestHarness.StartAsync(configureSettings: settings => settings.MaxPoolSize = 1);
        var first = await harness.Client.RentAsync(ClientTestHarness.Timeout());

        // Act: the second rent blocks until the first connection returns
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(ClientTestHarness.Timeout()).AsTask();
        await Task.Delay(100);
        pending.IsCompleted.ShouldBeFalse();

        await first.DisposeAsync();
        var second = await pending;

        // Assert
        second.ShouldBeSameAs(first);
        await second.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Errors: a server at capacity surfaces Unavailable on open")]
    public async Task RentAsync_ServerAtCapacity_ShouldSurfaceUnavailable()
    {
        // Arrange: MaxSessions=1, and a first client connection holding the slot
        await using var harness = await ClientTestHarness.StartAsync(configureServer: options => options.MaxSessions = 1);
        await using var holder = await harness.Client.RentAsync(ClientTestHarness.Timeout());

        // A second client with its own pool (the first pool would just queue)
        var second = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = ClientTestHarness.DatabaseName,
                EndPoint = harness.Listener.EndPoint,
            },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });

        await using (second)
        {
            // Act / Assert
            var exception = await Should.ThrowAsync<DatabaseClientException>(
                async () => await second.RentAsync(ClientTestHarness.Timeout()));

            exception.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Options: creation validates settings and factory")]
    public void Create_WithIncompleteOptions_ShouldThrow()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => DatabaseClient.Create(null!));
        Should.Throw<ArgumentException>(() => DatabaseClient.Create(new DatabaseClientOptions()));
        Should.Throw<ArgumentException>(() => DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app" }, // no endpoint, no factory
        }));
    }
}
