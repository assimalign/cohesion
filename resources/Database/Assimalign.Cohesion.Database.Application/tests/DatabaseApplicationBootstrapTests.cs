using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Application.Internal;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Application.Tests;

/// <summary>
/// Tests for the executable's bootstrap (#904): environment-configuration →
/// composition mapping, malformed-configuration rejection, and that the bootstrap
/// produces a startable application.
/// </summary>
public sealed class DatabaseApplicationBootstrapTests : IDisposable
{
    private readonly string _rootPath;

    public DatabaseApplicationBootstrapTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-db-app", Guid.NewGuid().ToString("N"));
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

    [Theory(DisplayName = "Cohesion Test [Database.Application] - Durability: Convention values map onto the commit-durability modes")]
    [InlineData(null, StorageCommitDurability.Synchronous)]
    [InlineData("", StorageCommitDurability.Synchronous)]
    [InlineData("full", StorageCommitDurability.Synchronous)]
    [InlineData("FULL", StorageCommitDurability.Synchronous)]
    [InlineData("synchronous", StorageCommitDurability.Synchronous)]
    [InlineData("grouped", StorageCommitDurability.Grouped)]
    [InlineData("Grouped", StorageCommitDurability.Grouped)]
    [InlineData("relaxed", StorageCommitDurability.Grouped)]
    public void MapDurability_WithConventionValue_ShouldMapToCommitDurability(string? value, StorageCommitDurability expected)
    {
        // Act / Assert
        DatabaseApplicationBootstrap.MapDurability(value).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - Durability: An unrecognized mode is rejected loudly")]
    public void MapDurability_WithUnknownValue_ShouldThrowFormatException()
    {
        // Act / Assert
        Should.Throw<FormatException>(() => DatabaseApplicationBootstrap.MapDurability("eventually"))
            .Message.ShouldContain("eventually");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - Compose: Configuration maps onto the composed stack")]
    public async Task Compose_WithConfiguration_ShouldMapOntoComposition()
    {
        // Arrange
        var configuration = new DatabaseHostConfiguration
        {
            DataPath = _rootPath,
            Port = 0,
            Durability = "grouped",
        };

        // Act
        await using var composition = DatabaseApplicationBootstrap.Compose(configuration);

        // Assert: one SQL engine fronted by the SQL server, registered on the
        // application, and a TCP listener bound to all interfaces on the configured
        // (OS-assigned) port.
        composition.Engine.Model.ShouldBe(EngineModel.Sql);
        composition.Server.Context.Engine.ShouldBeSameAs(composition.Engine);
        composition.Server.Engine.ShouldBeSameAs(composition.Engine);
        composition.Application.Context.Servers.ShouldHaveSingleItem().ShouldBeSameAs(composition.Server);
        composition.Application.Context.Engines.ShouldHaveSingleItem().ShouldBeSameAs(composition.Engine);

        var endpoint = composition.Listener.EndPoint.ShouldBeOfType<IPEndPoint>();
        endpoint.Address.ShouldBe(IPAddress.Any);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - Compose: A malformed durability mode is rejected")]
    public void Compose_WithMalformedDurability_ShouldThrowFormatException()
    {
        // Arrange
        var configuration = new DatabaseHostConfiguration { Durability = "not-a-mode" };

        // Act / Assert
        Should.Throw<FormatException>(() => DatabaseApplicationBootstrap.Compose(configuration));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - Compose: A null configuration is rejected")]
    public void Compose_WithNullConfiguration_ShouldThrowArgumentNull()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => DatabaseApplicationBootstrap.Compose(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Application] - Compose: The bootstrap builds a startable, stoppable application")]
    public async Task Compose_WithFileBackedConfiguration_ShouldBuildStartableApplication()
    {
        // Arrange: composing creates the engine — a data machine, operational (and
        // its data root on disk) before the host ever starts.
        var configuration = new DatabaseHostConfiguration { DataPath = _rootPath, Port = 0 };
        await using var composition = DatabaseApplicationBootstrap.Compose(configuration);

        composition.Engine.State.ShouldBe(EngineState.Running);
        Directory.Exists(_rootPath).ShouldBeTrue();

        // Act: start the composed host (provisioner → endpoint).
        await ((IHost)composition.Application).StartAsync(TestTimeout());

        // Assert: the host is serving.
        composition.Application.Context.State.ShouldBe(HostState.Started);

        // Act / Assert: a clean stop drains the endpoint to Stopped; the engine —
        // untouched by the host lifecycle — stays a live data machine until the
        // composition disposes it.
        await ((IHost)composition.Application).StopAsync(TestTimeout());
        composition.Application.Context.State.ShouldBe(HostState.Stopped);
        composition.Engine.State.ShouldBe(EngineState.Running);
    }

    private static System.Threading.CancellationToken TestTimeout(int seconds = 30)
        => new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
