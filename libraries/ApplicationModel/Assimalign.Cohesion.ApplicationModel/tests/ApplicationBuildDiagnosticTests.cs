using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[Collection(ConsoleOutputCollection.Name)]
public class ApplicationBuildDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Build reports one planner-to-compiler path per resource in declaration order")]
    public void Build_WithNamedAndGenericPlanners_ReportsOnePathPerResourceInDeclarationOrder()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway("kubernetes"));
        builder.AddResource(new NamedPlannedResource(
            TestManifestFactory.Create("appa-database"),
            "Database planner"));
        builder.AddResource(TestManifestFactory.Create("worker"));
        TextWriter originalOutput = Console.Out;
        TextWriter original = Console.Error;

        try
        {
            using var output = new StringWriter();
            using var standardOutput = new StringWriter();
            Console.SetError(output);
            Console.SetOut(standardOutput);

            // Act
            builder.Build();

            // Assert
            standardOutput.ToString().ShouldBeEmpty();
            string[] lines = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
            lines.ShouldBe(
            [
                "appa-database: Database planner → kubernetes compiler",
                "worker: GenericPlanner",
            ]);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(original);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Build rejects an invalid planner diagnostic label")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Database planner\nspoofed line")]
    public void Build_WithInvalidPlannerName_ThrowsActionableError(string plannerName)
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse("appa"), [])
            .UseGateway(new FakeGateway("kubernetes"));
        builder.AddResource(new NamedPlannedResource(
            TestManifestFactory.Create("worker"),
            plannerName));

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        error.Message.ShouldContain("worker");
        error.Message.ShouldContain(nameof(IPlannedResource.PlannerName));
        error.Message.ShouldContain("single-line");
    }
}
