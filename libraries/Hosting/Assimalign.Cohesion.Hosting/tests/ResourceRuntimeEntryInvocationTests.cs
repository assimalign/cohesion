using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

[Collection(nameof(SerialCollection))]
public class ResourceRuntimeEntryInvocationTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - Resource entry invocation: ";

    [Fact(DisplayName = DisplayPrefix + "Completion: Propagates an in-process host failure")]
    public async Task Completion_WhenInProcessHostFails_PropagatesFailureWithoutProcessEffects()
    {
        // Arrange
        int originalExitCode = System.Environment.ExitCode;
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            System.Environment.ExitCode = 23;
            Console.SetOut(output);

            using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntry(
                typeof(TestResource.Program).Assembly,
                []);
            IHost host = await invocation.HostReady.WaitAsync(cancellationTokenSource.Token);
            ResourceHostRunner runner = ((HostContext)host.Context).Runner
                .ShouldBeOfType<ResourceHostRunner>();
            ResourceHostOptions options = runner.Options;

            while (host.Context.State is not HostState.Started)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationTokenSource.Token);
            }

            host.Context.Shutdown();
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => invocation.Completion.WaitAsync(cancellationTokenSource.Token));

            // Assert
            options.RunMode.ShouldBe(ResourceHostRunMode.InProcess);
            exception.Message.ShouldBe("fixture stop failure");
            System.Environment.ExitCode.ShouldBe(23);
            output.ToString().ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(originalOutput);
            System.Environment.ExitCode = originalExitCode;
        }
    }
}
