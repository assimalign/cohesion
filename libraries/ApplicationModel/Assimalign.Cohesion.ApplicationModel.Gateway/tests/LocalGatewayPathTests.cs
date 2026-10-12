using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class LocalGatewayPathTests
{
    [WindowsProcessTheory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local launch: quoted and unquoted executable forms keep the same file and directory")]
    [InlineData("absolute", false)]
    [InlineData("absolute", true)]
    [InlineData("relative", false)]
    [InlineData("relative", true)]
    [InlineData("bare", false)]
    [InlineData("bare", true)]
    public async Task StartAsync_ShortExecutableForms_LaunchesSameFile(string form, bool quoted)
    {
        // Arrange
        string root = Directory.CreateTempSubdirectory("cohesion-launch-path-").FullName;
        string executable = Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost.exe");
        string command = form switch
        {
            "relative" => Path.Combine(".", Path.GetFileName(executable)),
            "bare" => Path.GetFileName(executable),
            _ => executable,
        };
        command = quoted ? $"\"{command}\"" : command;
        string capture = Path.Combine(root, "capture.txt");
        var gateway = new LocalGateway(new LocalGatewayOptions
        {
            BaseDirectory = AppContext.BaseDirectory,
            StateDirectory = root,
            StopGrace = TimeSpan.FromSeconds(1),
        });
        IApplicationBuilder builder = Application.CreateBuilder("launch-path", []).UseGateway(gateway);
        builder.AddExecutable("host", command, options => options
            .UseReadyMarker("cohesion-resource: ready")
            .AddEndpoint(new ResourceEndpoint("http", "http", 0))
            .UseRestartPolicy(RestartPolicy.Never)
            .AddEnvironment("TEST_LAUNCH_CAPTURE", capture));
        IApplicationGateway control = gateway;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            // Act
            await control.StartAsync(builder.Build().Model, cancellation.Token);

            // Assert
            File.ReadAllLines(capture).ShouldBe([executable, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)]);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }
}
