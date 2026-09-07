using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ExecutableApplicationModelResolverTests
{
    private const string DescribePidPathVariable = "COHESION_APPLICATION_MODEL_DESCRIBE_PID_PATH";
    private const string DescribeDocumentPathVariable = "COHESION_APPLICATION_MODEL_DESCRIBE_DOCUMENT_PATH";
    private const string RealizedDocumentPathVariable = "COHESION_APPLICATION_MODEL_REALIZED_DOCUMENT_PATH";
    private const string ArgumentsPathVariable = "COHESION_APPLICATION_MODEL_ARGUMENTS_PATH";
    private const string DescendantPidPathVariable = "COHESION_APPLICATION_MODEL_DESCENDANT_PID_PATH";

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Executable resolver re-describes only externals owned by that member")]
    public async Task ResolveAsync_RealizeNamesSpanMembers_ShouldForwardOnlyMatchingExternal()
    {
        // Arrange
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-application-model-describe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string documentPath = Path.Combine(temporaryDirectory, "model.json");
        string realizedPath = Path.Combine(temporaryDirectory, "model-realized.json");
        string argumentsPath = Path.Combine(temporaryDirectory, "arguments.txt");
        CreateMemberModel(realize: false).Save(documentPath);
        CreateMemberModel(realize: true).Save(realizedPath);
        string? originalDocument = Environment.GetEnvironmentVariable(DescribeDocumentPathVariable);
        string? originalRealized = Environment.GetEnvironmentVariable(RealizedDocumentPathVariable);
        string? originalArguments = Environment.GetEnvironmentVariable(ArgumentsPathVariable);
        Environment.SetEnvironmentVariable(DescribeDocumentPathVariable, documentPath);
        Environment.SetEnvironmentVariable(RealizedDocumentPathVariable, realizedPath);
        Environment.SetEnvironmentVariable(ArgumentsPathVariable, argumentsPath);

        try
        {
            IApplicationEnvironment environment = Application.CreateBuilder(
                ["--environment=Development"]).Environment;
            var context = new ApplicationModelResolutionContext(
                environment,
                GatewayRunMode.Apply,
                (ResourceName)"local",
                [(ResourceName)"peer-api", (ResourceName)"other-member-external"]);
            IApplicationModelResolver resolver = ApplicationModelResolvers.Executable(TestHostPath);

            // Act
            IApplicationModel model = await resolver.ResolveAsync(context);

            // Assert
            model.Resources.ShouldContain(resource => resource.Name == (ResourceName)"peer-api");
            model.Plans[1].Hints.ContainsKey("cohesion.external").ShouldBeFalse();
            string[] invocations = await File.ReadAllLinesAsync(argumentsPath);
            invocations.Length.ShouldBe(2);
            invocations[0].ShouldNotContain("--realize", Case.Insensitive);
            invocations[1].ShouldContain("--realize\u001fpeer-api", Case.Sensitive);
            invocations[1].ShouldNotContain("other-member-external", Case.Sensitive);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DescribeDocumentPathVariable, originalDocument);
            Environment.SetEnvironmentVariable(RealizedDocumentPathVariable, originalRealized);
            Environment.SetEnvironmentVariable(ArgumentsPathVariable, originalArguments);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Executable resolver cancellation terminates and drains describe process")]
    public async Task ResolveAsync_CallerCancellation_ShouldTerminateAndDrainDescribeProcess()
    {
        // Arrange
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-application-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string pidPath = Path.Combine(temporaryDirectory, "describe.pid");
        string? originalPidPath = Environment.GetEnvironmentVariable(DescribePidPathVariable);
        Environment.SetEnvironmentVariable(DescribePidPathVariable, pidPath);

        try
        {
            IApplicationEnvironment environment = Application.CreateBuilder(
                ["--environment=Development"]).Environment;
            var context = new ApplicationModelResolutionContext(
                environment,
                GatewayRunMode.Apply,
                (ResourceName)"test-gateway");
            IApplicationModelResolver resolver = ApplicationModelResolvers.Executable(TestHostPath);
            using var cancellation = new CancellationTokenSource();

            // Act
            Task<IApplicationModel> resolution = resolver.ResolveAsync(
                context,
                cancellation.Token).AsTask();
            await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(10));
            int processId = int.Parse(
                await File.ReadAllTextAsync(pidPath),
                CultureInfo.InvariantCulture);
            cancellation.Cancel();

            // Assert
            await Should.ThrowAsync<OperationCanceledException>(
                () => resolution.WaitAsync(TimeSpan.FromSeconds(10)));
            IsProcessRunning(processId).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DescribePidPathVariable, originalPidPath);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Executable resolver cancellation releases pipes inherited by an exited process descendant")]
    public async Task ResolveAsync_ExitedRootLeavesInheritedPipe_ShouldStillCancel()
    {
        // Arrange
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-application-model-descendant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string pidPath = Path.Combine(temporaryDirectory, "descendant.pid");
        string? originalPidPath = Environment.GetEnvironmentVariable(DescendantPidPathVariable);
        Environment.SetEnvironmentVariable(DescendantPidPathVariable, pidPath);
        int? descendantProcessId = null;

        try
        {
            IApplicationEnvironment environment = Application.CreateBuilder(
                ["--environment=Development"]).Environment;
            var context = new ApplicationModelResolutionContext(
                environment,
                GatewayRunMode.Apply,
                (ResourceName)"test-gateway");
            IApplicationModelResolver resolver = ApplicationModelResolvers.Executable(TestHostPath);
            using var cancellation = new CancellationTokenSource();
            Task<IApplicationModel> resolution = resolver.ResolveAsync(
                context,
                cancellation.Token).AsTask();
            await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(10));
            descendantProcessId = int.Parse(
                await File.ReadAllTextAsync(pidPath),
                CultureInfo.InvariantCulture);
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act
            cancellation.Cancel();

            // Assert
            await Should.ThrowAsync<OperationCanceledException>(
                () => resolution.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DescendantPidPathVariable, originalPidPath);
            if (descendantProcessId is int processId)
            {
                TerminateProcess(processId);
            }

            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
        }
    }

    private static ApplicationModelDocument CreateMemberModel(bool realize)
    {
        ResourceManifest peer = TestManifestFactory.Create("peer-api", "peer");
        var declaration = new ExternalResourceDeclaration(
            "peer-api",
            "peer",
            ["control"],
            optional: false,
            peer,
            [peer]);
        string[] args = realize
            ? ["--environment=Development", "--realize=peer-api"]
            : ["--environment=Development"];
        IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("member"),
                args)
            .UseGateway(new FakeGateway("local"));
        builder.AddResource(TestManifestFactory.Create("api", "member"));
        builder.AddExternal(declaration);
        return ApplicationModelDocument.Create(builder.Build().Model);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TerminateProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (ArgumentException)
        {
            // The descendant already exited.
        }
        catch (InvalidOperationException)
        {
            // The descendant exited while the test was cleaning it up.
        }
    }

    private static string TestHostPath => Path.Combine(
        AppContext.BaseDirectory,
        "Assimalign.Cohesion.ApplicationModel.TestHost"
        + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
}
