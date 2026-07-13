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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Composition: defaults claim the engine's five workers plus the engine and endpoint services")]
    public async Task Application_Defaults_ShouldComposeEngineWorkersAndEndpointServices()
    {
        // Arrange: the harness hands the application an unstarted engine, so every
        // worker slot claims its worker at composition time.
        await using var harness = await DatabaseHostTestHarness.CreateAsync();

        // Assert: 1 engine lifecycle service + 5 claimed worker slots + the endpoint.
        int count = 0;
        foreach (IHostService _ in harness.Application.Context.HostedServices)
        {
            count++;
        }

        count.ShouldBe(7);
        harness.Application.Context.Engines.ShouldHaveSingleItem();

        // Every engine worker is claimed by the host (a second claim fails).
        foreach (IDatabaseEngineWorker worker in harness.Engine.Workers)
        {
            worker.TryClaim().ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Composition: disabling slots hands those workers back to the engine")]
    public async Task Application_DisabledDurabilitySlots_ShouldLeaveWorkersWithEngine()
    {
        // Arrange: disable the two dedicated-thread slots; the engine self-schedules
        // those workers at start instead (the work is never lost — R10).
        await using var harness = await DatabaseHostTestHarness.CreateAsync(options =>
        {
            options.Workers.WriteAheadFlush.Enabled = false;
            options.Workers.PageWriteBack.Enabled = false;
        });

        // Assert: 1 engine service + 3 remaining worker slots + the endpoint.
        int count = 0;
        foreach (IHostService _ in harness.Application.Context.HostedServices)
        {
            count++;
        }

        count.ShouldBe(5);

        // The disabled kinds stay unclaimed until the engine starts.
        foreach (IDatabaseEngineWorker worker in harness.Engine.Workers)
        {
            if (worker.Kind is DatabaseEngineWorkerKind.WriteAheadFlush or DatabaseEngineWorkerKind.PageWriteBack)
            {
                worker.TryClaim().ShouldBeTrue();
                worker.Release();
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Workers: host-mapped workers pump while the host runs and stop with it")]
    public async Task Lifecycle_HostMappedWorkers_ShouldPumpBetweenStartAndStop()
    {
        // Arrange: a recording engine exposing one worker per execution-menu member.
        var log = new List<string>();
        var engine = new RecordingEngine(log);
        var flushWorker = new RecordingWorker(DatabaseEngineWorkerKind.WriteAheadFlush, TimeSpan.FromMilliseconds(10));
        var checkpointWorker = new RecordingWorker(DatabaseEngineWorkerKind.Checkpoint, TimeSpan.FromMilliseconds(10));
        engine.AddWorker(flushWorker);
        engine.AddWorker(checkpointWorker);

        var options = new DatabaseApplicationOptions();
        options.Engines.Add(engine);
        var application = new DatabaseApplication(options);

        // Both workers were claimed at composition time.
        flushWorker.TryClaim().ShouldBeFalse();
        checkpointWorker.TryClaim().ShouldBeFalse();

        // Act: start the host; the dedicated-thread slot enters Run and the timer
        // slot ticks RunIteration.
        await ((IHost)application).StartAsync(DatabaseHostTestHarness.Timeout());

        flushWorker.RunEntered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        checkpointWorker.IterationRan.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // Act: stop the host; both pumps exit.
        await ((IHost)application).StopAsync(DatabaseHostTestHarness.Timeout());

        // Assert
        flushWorker.RunExited.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
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
