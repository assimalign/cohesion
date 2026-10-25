using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

public sealed class ResourceCommandSdkIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - Resource v2 refuses retired command declarations")]
    public async Task Build_WithCommand_ShouldReportMigrationError()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("EnabledGeneric");
        string project = workspace.ProjectFile("EnabledGeneric");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
            "<ItemGroup><CohesionCommand Include=\"worker.start\" /></ItemGroup></Project>", StringComparison.Ordinal));

        // Act
        DotNetBuildResult result = await workspace.BuildAsync("EnabledGeneric", timeout.Token);

        // Assert
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ShouldContain("CohesionCommand is no longer supported.", Case.Sensitive);
    }
}
