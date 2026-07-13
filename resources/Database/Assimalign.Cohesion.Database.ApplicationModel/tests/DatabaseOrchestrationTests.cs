using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

/// <summary>
/// Integration tests for the database orchestration manifest (#853): a
/// <see cref="DatabaseResource"/> composed with <c>AddDatabase(...).DependsOn(...)</c>
/// realizes through the generic gateway algorithm in dependency order, tears down in
/// reverse, injects its observed endpoint, and blocks dependents when it fails.
/// </summary>
public class DatabaseOrchestrationTests
{
    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - Orchestration: a dependent realizes after the database, which publishes its observed endpoint")]
    public async Task StartAsync_DependentOnDatabase_ProvisionsDatabaseFirstAndInjectsObservedEndpoint()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new RecordingStateManager();
        var gateway = new RecordingGateway(state, new RecordingController(reconciled, deleted));

        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor database = builder.AddDatabase("orders-db");
        IApplicationResourceDescriptor api = builder.AddResource(new FakeApplicationResource("api"));
        api.DependsOn(database);
        IApplicationModel model = builder.Build().Model;

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model);

        // Assert: the database realized before its dependent, reached Running, and its
        // platform-allocated endpoint is observable for the dependent to consume.
        reconciled.ShouldBe(new[] { "orders-db", "api" });
        state.GetState(database.Resource.Id).ShouldBe(ResourceLifecycle.Running);

        ResourceEndpoint observed = state.GetObservedEndpoints(database.Resource.Id).ShouldHaveSingleItem();
        observed.Scheme.ShouldBe(DatabaseResource.EndpointScheme);
        observed.Port.ShouldBe(61000);
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - Orchestration: teardown stops the dependent before the database")]
    public async Task StopAsync_AfterStart_TearsDownInReverseOrder()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new RecordingStateManager();
        var gateway = new RecordingGateway(state, new RecordingController(reconciled, deleted));

        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor database = builder.AddDatabase("orders-db");
        IApplicationResourceDescriptor api = builder.AddResource(new FakeApplicationResource("api"));
        api.DependsOn(database);
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;

        await control.StartAsync(model);

        // Act
        await control.StopAsync();

        // Assert
        deleted.ShouldBe(new[] { "api", "orders-db" });
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - Orchestration: a failed database blocks its dependents and aborts startup")]
    public async Task StartAsync_WhenDatabaseFails_BlocksDependentsAndThrows()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new RecordingStateManager();
        var failing = new HashSet<string> { "orders-db" };
        var gateway = new RecordingGateway(state, new RecordingController(reconciled, deleted, failing));

        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor database = builder.AddDatabase("orders-db");
        IApplicationResourceDescriptor api = builder.AddResource(new FakeApplicationResource("api"));
        api.DependsOn(database);
        IApplicationModel model = builder.Build().Model;

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model));

        reconciled.ShouldBe(new[] { "orders-db" });  // the dependent is never reconciled
        state.GetState(api.Resource.Id).ShouldBe(ResourceLifecycle.Blocked);
    }
}
