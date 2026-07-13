using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Tests for the database host composition (#166): the host composes the execution
/// menu, runs the wire endpoint, serves a query end-to-end, and drains on stop.
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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Composition: defaults compose the engine, the durability slots, and the endpoint")]
    public async Task Application_Defaults_ShouldComposeEngineDurabilityAndEndpointServices()
    {
        // Arrange
        await using var harness = await DatabaseHostTestHarness.CreateAsync();

        // Assert: the engine lifecycle service + WriteAheadFlush + PageWriter + the endpoint
        int count = 0;
        foreach (IHostService _ in harness.Application.Context.HostedServices)
        {
            count++;
        }

        count.ShouldBe(4);
        harness.Application.Context.Engines.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Composition: disabling the durability slots composes the engine and endpoint only")]
    public async Task Application_DisabledDurabilitySlots_ShouldComposeEngineAndEndpointOnly()
    {
        // Arrange
        await using var harness = await DatabaseHostTestHarness.CreateAsync(options =>
        {
            options.EnableWriteAheadFlushService = false;
            options.EnablePageWriterService = false;
        });

        // Assert
        int count = 0;
        foreach (IHostService _ in harness.Application.Context.HostedServices)
        {
            count++;
        }

        count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: engines start before the endpoint and stop after it drains")]
    public async Task Lifecycle_EngineAndEndpoint_ShouldStartEngineFirstAndStopItLast()
    {
        // Arrange: a recording engine + recording server capture the relative order of
        // lifecycle calls made by the host.
        var log = new List<string>();
        var engine = new RecordingEngine(log);
        var server = new RecordingServer(log);

        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        options.Server = server;

        var application = new DatabaseApplication(options);

        // Act
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert: engine start precedes server start; server stop precedes engine stop.
        log.IndexOf("engine:start").ShouldBeLessThan(log.IndexOf("server:start"));
        log.IndexOf("server:stop").ShouldBeLessThan(log.IndexOf("engine:stop"));
        engine.State.ShouldBe(EngineState.Stopped);
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

        // Assert
        harness.Application.Context.State.ShouldBe(HostState.Stopped);
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
