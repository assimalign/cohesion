using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[Collection(ApplicationEnvironmentCollection.Name)]
public sealed class ApplicationEnvironmentTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Environment flags distinguish Local from deployed Development")]
    [InlineData(AppEnvironment.Keys.Local, true, false)]
    [InlineData("lOcAl", true, false)]
    [InlineData(AppEnvironment.Keys.Development, false, true)]
    [InlineData("dEvElOpMeNt", false, true)]
    [InlineData(AppEnvironment.Keys.Staging, false, false)]
    [InlineData(AppEnvironment.Keys.Production, false, false)]
    [InlineData("Testing", false, false)]
    public void CreateBuilder_ExplicitEnvironment_ShouldSetIndependentFlags(string name, bool isLocal, bool isDevelopment)
    {
        // Act
        IApplicationEnvironment environment = Application.CreateBuilder(["--environment", name]).Environment;

        // Assert
        environment.Name.ToString().ShouldBe(name);
        environment.IsLocal.ShouldBe(isLocal);
        environment.IsDevelopment.ShouldBe(isDevelopment);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Unset apphost environment is Local before and after gateway selection")]
    [InlineData("local", AppEnvironment.Keys.Local)]
    [InlineData("LoCaL", AppEnvironment.Keys.Local)]
    [InlineData("inprocess", AppEnvironment.Keys.Local)]
    [InlineData("InPrOcEsS", AppEnvironment.Keys.Local)]
    [InlineData("docker", AppEnvironment.Keys.Local)]
    [InlineData("kubernetes", AppEnvironment.Keys.Local)]
    public void SelectGateway_UnsetEnvironment_ShouldUseGatewayDefault(string gateway, string expected)
    {
        WithEnvironment(null, null, () =>
        {
            // Arrange
            IApplicationBuilder builder = Application.CreateBuilder("appa", []);
            builder.AddResource(TestManifestFactory.Create("worker"));
            builder.Environment.Name.ToString().ShouldBe(AppEnvironment.Keys.Local);
            builder.Environment.IsLocal.ShouldBeTrue();
            AppEnvironment.GetEnvironmentName().ShouldBe(AppEnvironment.Keys.Production);

            // Act
            builder.UseGateway(new FakeMultiModelGateway(gateway));
            IApplicationSet set = Application.CreateSet(new FakeMultiModelGateway(gateway), []);
            IApplicationModel model = builder.Build().Model;

            // Assert
            builder.Environment.Name.ToString().ShouldBe(expected);
            set.Environment.Name.ToString().ShouldBe(expected);
            model.Plans.ShouldHaveSingleItem().Container.Environment[ResourceEnvironment.Environment]
                .ShouldBe(expected);
        });
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Gateway selection preserves explicit environment options")]
    [InlineData("local", AppEnvironment.Keys.Development)]
    [InlineData("inprocess", AppEnvironment.Keys.Production)]
    [InlineData("docker", AppEnvironment.Keys.Local)]
    [InlineData("kubernetes", AppEnvironment.Keys.Staging)]
    public void SelectGateway_ExplicitOption_ShouldPreserveBothForms(string gateway, string expected)
    {
        WithEnvironment(AppEnvironment.Keys.Production, AppEnvironment.Keys.Staging, () =>
        {
            foreach (string[] args in new[] { new[] { "--environment", expected }, new[] { "--environment=" + expected } })
            {
                // Act
                IApplicationBuilder builder = Application.CreateBuilder("appa", args).UseGateway(new FakeMultiModelGateway(gateway));
                IApplicationSet set = Application.CreateSet(new FakeMultiModelGateway(gateway), args);

                // Assert
                builder.Environment.Name.ToString().ShouldBe(expected);
                set.Environment.Name.ToString().ShouldBe(expected);
            }
        });
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Gateway selection preserves process environment precedence")]
    [InlineData(AppEnvironment.Keys.Development, AppEnvironment.Keys.Staging, AppEnvironment.Keys.Development)]
    [InlineData(null, AppEnvironment.Keys.Development, AppEnvironment.Keys.Development)]
    [InlineData(AppEnvironment.Keys.Production, null, AppEnvironment.Keys.Production)]
    [InlineData(AppEnvironment.Keys.Local, AppEnvironment.Keys.Production, AppEnvironment.Keys.Local)]
    [InlineData(" ", AppEnvironment.Keys.Development, " ")]
    public void SelectGateway_ProcessEnvironment_ShouldPreservePrecedence(string? cohesion, string? dotnet, string expected)
    {
        WithEnvironment(cohesion, dotnet, () =>
        {
            foreach (string gateway in new[] { "local", "inprocess", "docker", "kubernetes" })
            {
                // Act
                IApplicationBuilder builder = Application.CreateBuilder("appa", []).UseGateway(new FakeMultiModelGateway(gateway));
                IApplicationSet set = Application.CreateSet(new FakeMultiModelGateway(gateway), []);

                // Assert
                builder.Environment.Name.ToString().ShouldBe(expected);
                set.Environment.Name.ToString().ShouldBe(expected);
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Selecting Docker preserves the apphost Local default")]
    public void UseGateway_ReselectDocker_ShouldPreserveImplicitLocalDefault()
    {
        WithEnvironment(null, null, () =>
        {
            // Arrange
            IApplicationBuilder builder = Application.CreateBuilder("appa", []).UseGateway(new FakeGateway("local"));
            builder.Environment.IsLocal.ShouldBeTrue();

            // Act
            builder.UseGateway(new FakeGateway("docker"));

            // Assert
            builder.Environment.Name.ToString().ShouldBe(AppEnvironment.Keys.Local);
            builder.Environment.IsLocal.ShouldBeTrue();
        });
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Blank process values permit the Local gateway default")]
    [InlineData("local")]
    [InlineData("inprocess")]
    public void SelectGateway_WhitespaceProcessValues_ShouldUseLocalDefault(string gateway)
    {
        WithEnvironment(" ", "\t", () =>
        {
            // Arrange
            IApplicationBuilder builder = Application.CreateBuilder("appa", []);
            builder.Environment.Name.ToString().ShouldBe(" ");

            // Act
            builder.UseGateway(new FakeMultiModelGateway(gateway));
            IApplicationSet set = Application.CreateSet(new FakeMultiModelGateway(gateway), []);

            // Assert
            builder.Environment.IsLocal.ShouldBeTrue();
            set.Environment.IsLocal.ShouldBeTrue();
        });
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Local unnamed builders retain entry-assembly slug fallback")]
    public void Build_UnnamedLocalBuilder_ShouldUseEntryAssemblySlug()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(["--environment=Local"]).UseGateway(new FakeGateway("local"));
        builder.AddResource(new FakeResource("worker"));

        // Act
        IApplicationModel model = builder.Build().Model;

        // Assert
        model.Name.ToString().ShouldNotBeNullOrWhiteSpace();
        model.Name.ToString().ShouldMatch("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$");
        model.Environment.IsLocal.ShouldBeTrue();
    }

    private static void WithEnvironment(string? cohesion, string? dotnet, Action action)
    {
        string? originalCohesion = Environment.GetEnvironmentVariable(AppEnvironment.Keys.EnvironmentKey);
        string? originalDotnet = Environment.GetEnvironmentVariable(AppEnvironment.Keys.DotNetEnvironmentKey);
        try
        {
            Environment.SetEnvironmentVariable(AppEnvironment.Keys.EnvironmentKey, cohesion);
            Environment.SetEnvironmentVariable(AppEnvironment.Keys.DotNetEnvironmentKey, dotnet);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppEnvironment.Keys.EnvironmentKey, originalCohesion);
            Environment.SetEnvironmentVariable(AppEnvironment.Keys.DotNetEnvironmentKey, originalDotnet);
        }
    }
}
