using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed partial class DatabaseHostingEventSourceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health: skipped assignments emit one explanatory event (#1790)")]
    [InlineData("https", "127.0.0.1")]
    [InlineData("tcp", "127.0.0.1")]
    [InlineData("http", "example.test")]
    public async Task Build_UnsupportedHealthAssignment_ShouldReportSkipOnce(string scheme, string host)
    {
        // Arrange
        using var recorder = new HostingEventRecorder(EventLevel.Informational);
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "test",
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = host,
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = "5740",
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = scheme,
        }, CancellationToken.None));

        // Act
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();

        // Assert
        var skipped = Event(recorder, 18, "HealthEndpointAssignmentSkipped", "assignment", "endpoint", "scheme");
        skipped.Level.ShouldBe(EventLevel.Informational);
        skipped.Payload.ShouldBe(["http", $"{scheme}://{host}:5740", scheme]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Health: managed override reports both endpoints once (#1790)")]
    public async Task Build_ManagedHealthOverride_ShouldReportBothEndpoints()
    {
        // Arrange
        using var recorder = new HostingEventRecorder(EventLevel.Informational);
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "test",
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = "5740",
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = "http",
        }, CancellationToken.None));
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddHealthEndpoint(new Uri("http://127.0.0.1:5741"));

        // Act
        await using DatabaseApplication application = builder.Build();

        // Assert
        var replaced = Event(recorder, 19, "HealthEndpointOverridden", "explicitEndpoint", "platformEndpoint");
        replaced.Level.ShouldBe(EventLevel.Informational);
        replaced.Payload.ShouldBe(["http://127.0.0.1:5741", "http://127.0.0.1:5740"]);
    }
}
