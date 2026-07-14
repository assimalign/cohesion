using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Tests for the composition-only database host: the application wraps registered
/// servers as endpoint services (started last, drained first), serves a query
/// end-to-end through a composed per-model server, and drains on stop.
/// </summary>
public class DatabaseApplicationTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Host: a started host serves a query over the composed endpoint")]
    public async Task StartHost_WithEndpointService_ShouldServeQueryEndToEnd()
    {
        // Arrange
        await using var harness = await DatabaseHostTestHarness.CreateAsync();
        await harness.StartHostAsync(DatabaseHostTestHarness.Timeout());

        // Act: the host started the wire server; drive a served round-trip through the client
        await using var connection = await harness.Client.RentAsync(DatabaseHostTestHarness.Timeout());
        DatabaseClientResult result = await connection.ExecuteAsync("SELECT id, name FROM users ORDER BY id", cancellationToken: DatabaseHostTestHarness.Timeout());

        // Assert
        harness.Application.Context.State.ShouldBe(HostState.Started);
        result.Rows.Count.ShouldBe(2);
        result.Rows[0].ShouldBe([1, "ada"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Composition: the application is composition-only — one endpoint service per server, nothing else")]
    public async Task Application_Defaults_ShouldComposeOneEndpointServicePerServer()
    {
        // Arrange: the harness composes one SQL server; the engine takes no part in
        // the host lifecycle (it is a data machine, operational from creation).
        await using var harness = await DatabaseHostTestHarness.CreateAsync();

        // Assert: exactly one hosted service — the endpoint wrapper for the server.
        int count = 0;
        foreach (IHostService _ in harness.Application.Context.HostedServices)
        {
            count++;
        }

        count.ShouldBe(1);
        harness.Application.Context.Servers.ShouldHaveSingleItem().ShouldBeSameAs(harness.Server);
        harness.Application.Context.Engines.ShouldBeEmpty();
        harness.Server.Context.Engine.ShouldBeSameAs(harness.Engine);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: services start before the servers and stop after they drain")]
    public async Task Lifecycle_ServicesAndServers_ShouldStartServersLastAndStopThemFirst()
    {
        // Arrange: a recording service + two recording servers capture the relative
        // order of lifecycle calls made by the host (servers are per-model, so an
        // application may run several).
        var log = new List<string>();
        var options = new DatabaseApplicationOptions();

        options.Services.Add(new RecordingService(log, "provisioner"));
        options.Servers.Add(new RecordingServer(log, "sql-server"));
        options.Servers.Add(new RecordingServer(log, "docs-server"));

        var application = new DatabaseApplication(options);

        // Act
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the service starts ahead of both servers; both servers stop
        // (drain) before the service stops; server order follows registration.
        log.IndexOf("provisioner:start").ShouldBeLessThan(log.IndexOf("sql-server:start"));
        log.IndexOf("sql-server:start").ShouldBeLessThan(log.IndexOf("docs-server:start"));
        log.IndexOf("docs-server:stop").ShouldBeLessThan(log.IndexOf("sql-server:stop"));
        log.IndexOf("sql-server:stop").ShouldBeLessThan(log.IndexOf("provisioner:stop"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: stopping the host drains and reaches the stopped state")]
    public async Task StopHost_AfterServing_ShouldDrainToStoppedState()
    {
        // Arrange
        await using var harness = await DatabaseHostTestHarness.CreateAsync();
        await harness.StartHostAsync(DatabaseHostTestHarness.Timeout());

        await using (var connection = await harness.Client.RentAsync(DatabaseHostTestHarness.Timeout()))
        {
            await connection.ExecuteAsync("SELECT id FROM users WHERE id = 1", cancellationToken: DatabaseHostTestHarness.Timeout());
        }

        // Act
        await harness.StopHostAsync(DatabaseHostTestHarness.Timeout());

        // Assert: the host drained; the engine — untouched by the host lifecycle —
        // is still a live data machine.
        harness.Application.Context.State.ShouldBe(HostState.Stopped);
        harness.Engine.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Config: environment conventions bind, and unset variables stay null")]
    public void Configuration_FromEnvironment_ShouldBindConventionalVariables()
    {
        // Arrange
        Environment.SetEnvironmentVariable(DatabaseHostConfiguration.DataPathVariable, "/var/lib/cohesion-db");
        Environment.SetEnvironmentVariable(DatabaseHostConfiguration.PortVariable, "5999");
        Environment.SetEnvironmentVariable(DatabaseHostConfiguration.DurabilityVariable, null);

        try
        {
            // Act
            DatabaseHostConfiguration configuration = DatabaseHostConfiguration.FromEnvironment();

            // Assert
            configuration.DataPath.ShouldBe("/var/lib/cohesion-db");
            configuration.Port.ShouldBe(5999);
            configuration.Durability.ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabaseHostConfiguration.DataPathVariable, null);
            Environment.SetEnvironmentVariable(DatabaseHostConfiguration.PortVariable, null);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Config: a malformed port variable is rejected")]
    public void Configuration_FromEnvironment_WithBadPort_ShouldThrow()
    {
        // Arrange
        Environment.SetEnvironmentVariable(DatabaseHostConfiguration.PortVariable, "not-a-port");

        try
        {
            // Act / Assert
            Should.Throw<FormatException>(() => DatabaseHostConfiguration.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabaseHostConfiguration.PortVariable, null);
        }
    }
}
