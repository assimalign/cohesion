using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourceCommandObservationTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Export: rejects malformed command observations")]
    [InlineData("target")]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("owner")]
    [InlineData("key")]
    [InlineData("status")]
    [InlineData("duplicate")]
    public void Create_WithInvalidCommandObservation_ShouldReject(string invalid)
    {
        // Arrange
        IApplicationModel model = CreateModel();
        var observation = new ResourceCommandObservation(
            invalid == "target" ? "unknown" : "worker",
            invalid == "id" ? " " : "command-id",
            invalid == "kind" ? " " : "test.create",
            invalid == "owner" ? " " : "remote-owner",
            invalid == "key" ? " " : "orders",
            invalid == "status" ? (ResourceCommandStatus)999 : ResourceCommandStatus.Rejected,
            "Unsupported command kind.");
        ResourceCommandObservation[] observations = invalid == "duplicate" ? [observation, observation] : [observation];

        // Act
        InvalidDataException error = Should.Throw<InvalidDataException>(() =>
            ApplicationExportDocument.Create(model, "1.0", commands: observations));

        // Assert
        error.Message.ShouldContain("command");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Export: preserves foreign rejected observations without desired declarations")]
    public void Create_WithForeignRejectedCommand_ShouldKeepAuditWithoutPayload()
    {
        // Arrange
        IApplicationModel model = CreateModel();
        var observation = new ResourceCommandObservation("worker", "command-id", "unsupported.kind",
            "remote-owner", "orders", ResourceCommandStatus.Rejected, "Unsupported command kind.");
        ResourceCommandObservation[] source = [observation];

        // Act
        ApplicationExportDocument export = ApplicationExportDocument.Create(model, "1.0", commands: source);
        source[0] = observation with { Owner = "changed" };
        using var stream = new MemoryStream();
        export.Save(stream);
        stream.Position = 0;
        ApplicationExportDocument imported = ApplicationExportDocument.Load(stream);

        // Assert
        imported.Commands[0].ShouldBe(observation);
        imported.Model.Commands.ShouldBeEmpty();
        imported.Commands[0].Status.ShouldBe(ResourceCommandStatus.Rejected);
    }

    private static IApplicationModel CreateModel()
    {
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", [])
            .UseGateway(new FakeGateway());
        builder.AddResource(TestManifestFactory.Create());
        return builder.Build().Model;
    }
}
