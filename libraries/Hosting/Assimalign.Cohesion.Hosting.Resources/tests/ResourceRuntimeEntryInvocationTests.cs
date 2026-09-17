using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

[Collection(nameof(SerialCollection))]
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2026",
    Justification = "Tests invoke compiler-rooted fixture entry points whose assemblies are retained by direct references.")]
public class ResourceRuntimeEntryInvocationTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - Resource entry invocation: ";
    private static readonly AsyncLocal<DirectEntryInvocationState?> DirectEntryInvocation = new();

    [Fact(DisplayName = DisplayPrefix + "Registration: Reports a generated entry registration")]
    public void IsEntryRegistered_WhenGeneratedEntryIsRegistered_ReturnsTrue()
    {
        // Arrange
        Assembly assembly = typeof(TestResource.Program).Assembly;

        // Act
        bool isRegistered = ResourceRuntime.IsEntryRegistered(assembly);

        // Assert
        isRegistered.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Registration: Preserves the registered invocation guard")]
    public void InvokeEntry_WhenEntryIsNotRegistered_Throws()
    {
        // Arrange
        var assembly = new DirectEntryAssembly();
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ResourceRuntime.InvokeEntry(assembly, []));

        // Assert
        ResourceRuntime.IsEntryRegistered(assembly).ShouldBeFalse();
        exception.Message.ShouldBe(
            "Resource assembly 'DirectEntryAssembly' did not register an enabled resource entry point.");
    }

    [Fact(DisplayName = DisplayPrefix + "Fallback: Invokes an unregistered compiler-rooted entry point")]
    public async Task InvokeEntryPoint_WhenEntryIsNotRegistered_InvokesInExplicitScope()
    {
        // Arrange
        var assembly = new DirectEntryAssembly();
        var context = new ResourceContext(resourceName: "direct-entry");
        string[] arguments = ["original"];
        var state = new DirectEntryInvocationState
        {
            ControlPlaneLookupAssembly = typeof(ResourceRuntimeEntryInvocationTests).Assembly,
        };
        ResourceRuntime.RegisterControlPlane(
            assembly,
            static () => ResourceControlPlane.Create());
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        DirectEntryInvocation.Value = state;

        try
        {
            using IDisposable scope = ResourceRuntime.CreateScope(context);

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntryPoint(assembly, arguments);
            await state.Entered.Task.WaitAsync(cancellationTokenSource.Token);
            arguments[0] = "changed";
            state.Continue.TrySetResult(true);
            IHost host = await invocation.HostReady.WaitAsync(cancellationTokenSource.Token);
            await invocation.Completion.WaitAsync(cancellationTokenSource.Token);

            // Assert
            ResourceRuntime.IsEntryRegistered(assembly).ShouldBeFalse();
            state.Context.ShouldBeSameAs(context);
            state.Arguments.ShouldBe(["original"]);
            state.ControlPlaneResolved.ShouldBeTrue();
            host.ShouldBeSameAs(state.Host);
            ((HostContext)host.Context).Runner
                .ShouldBeOfType<ResourceHostRunner>()
                .Options.RunMode.ShouldBe(ResourceHostRunMode.InProcess);
        }
        finally
        {
            state.Continue.TrySetResult(true);
            DirectEntryInvocation.Value = null;
            if (state.Host is not null)
            {
                await state.Host.DisposeAsync();
            }
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness: Waits for RunAsync after synchronous composition")]
    public async Task HostReady_WhenSynchronousCompositionFollowsHostBuild_WaitsForRunInvocation()
    {
        // Arrange
        var hostBuilt = new TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueComposition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool compositionCompleted = false;
        TestResource.Program.InvocationOverride = host =>
        {
            hostBuilt.TrySetResult(host);
            continueComposition.Task.GetAwaiter().GetResult();
            compositionCompleted = true;
            return host.RunAsync(cancellationTokenSource.Token);
        };

        try
        {
            using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntry(
                typeof(TestResource.Program).Assembly,
                []);
            IHost builtHost = await hostBuilt.Task.WaitAsync(cancellationTokenSource.Token);

            // Assert
            invocation.HostReady.IsCompleted.ShouldBeFalse();

            continueComposition.TrySetResult(true);
            IHost host = await invocation.HostReady.WaitAsync(cancellationTokenSource.Token);

            compositionCompleted.ShouldBeTrue();
            host.ShouldBeSameAs(builtHost);

            host.Context.Shutdown();
            await invocation.Completion.WaitAsync(cancellationTokenSource.Token);
        }
        finally
        {
            continueComposition.TrySetResult(true);
            await cancellationTokenSource.CancelAsync();
            TestResource.Program.InvocationOverride = null;
            if (hostBuilt.Task.IsCompletedSuccessfully)
            {
                IHost completedHost = await hostBuilt.Task;
                await completedHost.DisposeAsync();
            }
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness: Surrenders an idle host after successful entry completion")]
    public async Task HostReady_WhenEntryCompletesSuccessfullyWithIdleHost_Completes()
    {
        // Arrange
        var overrideInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeEntryPoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IHost? builtHost = null;
        TestResource.Program.InvocationOverride = host =>
        {
            builtHost = host;
            overrideInvoked.TrySetResult(true);
            return completeEntryPoint.Task;
        };

        try
        {
            using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntry(
                typeof(TestResource.Program).Assembly,
                []);
            await overrideInvoked.Task.WaitAsync(cancellationTokenSource.Token);

            invocation.HostReady.IsCompleted.ShouldBeFalse();
            completeEntryPoint.TrySetResult(true);
            IHost host = await invocation.HostReady.WaitAsync(cancellationTokenSource.Token);

            // Assert
            host.ShouldBeSameAs(builtHost);
            host.Context.State.ShouldBe(HostState.Idle);

            await invocation.Completion.WaitAsync(cancellationTokenSource.Token);
        }
        finally
        {
            completeEntryPoint.TrySetResult(true);
            TestResource.Program.InvocationOverride = null;
            if (builtHost is not null)
            {
                await builtHost.DisposeAsync();
            }
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness: Releases a surrendered host when composition fails")]
    public async Task HostReady_WhenEntryFailsAfterHostBuild_ReleasesHostForCleanup()
    {
        // Arrange
        var overrideInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IHost? builtHost = null;
        TestResource.Program.InvocationOverride = host =>
        {
            builtHost = host;
            overrideInvoked.TrySetResult(true);
            return Task.FromException(new InvalidOperationException("composition failed"));
        };

        try
        {
            using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntry(
                typeof(TestResource.Program).Assembly,
                []);
            await overrideInvoked.Task.WaitAsync(cancellationTokenSource.Token);
            IHost host = await invocation.HostReady.WaitAsync(cancellationTokenSource.Token);
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => invocation.Completion.WaitAsync(cancellationTokenSource.Token));

            // Assert
            host.ShouldBeSameAs(builtHost);
            host.Context.State.ShouldBe(HostState.Idle);
            exception.Message.ShouldBe("composition failed");
        }
        finally
        {
            TestResource.Program.InvocationOverride = null;
            if (builtHost is not null)
            {
                await builtHost.DisposeAsync();
            }
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness: Ignores hosts built before entry invocation")]
    public async Task HostReady_WhenHostWasBuiltBeforeInvocation_ReturnsInvokedHost()
    {
        // Arrange
        var unrelatedHost = new TestHost(new TestHostOptions());
        IHost? invokedHost = null;
        TestResource.Program.InvocationOverride = host =>
        {
            invokedHost = host;
            return Task.CompletedTask;
        };

        try
        {
            using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());
            ResourceRuntime.HostBuilt(unrelatedHost, ResourceControlPlane.Create());

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntry(
                typeof(TestResource.Program).Assembly,
                []);
            IHost surrendered = await invocation.HostReady.WaitAsync(TimeSpan.FromSeconds(10));
            await invocation.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            surrendered.ShouldBeSameAs(invokedHost);
            surrendered.ShouldNotBeSameAs(unrelatedHost);
        }
        finally
        {
            TestResource.Program.InvocationOverride = null;
            await ((IHost)unrelatedHost).DisposeAsync();
            if (invokedHost is not null)
            {
                await invokedHost.DisposeAsync();
            }
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Readiness: Refuses a second surrendered host")]
    public async Task HostReady_WhenEntryBuildsTwoHosts_RefusesSecondHost()
    {
        // Arrange
        IHost? firstHost = null;
        var secondHost = new TestHost(new TestHostOptions());
        TestResource.Program.InvocationOverride = host =>
        {
            firstHost = host;
            ResourceRuntime.HostBuilt(secondHost, ResourceControlPlane.Create());
            return Task.CompletedTask;
        };

        try
        {
            using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntry(
                typeof(TestResource.Program).Assembly,
                []);
            IHost surrendered = await invocation.HostReady.WaitAsync(TimeSpan.FromSeconds(10));
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => invocation.Completion.WaitAsync(TimeSpan.FromSeconds(10)));

            // Assert
            surrendered.ShouldBeSameAs(firstHost);
            exception.Message.ShouldContain("surrendered more than one host");
        }
        finally
        {
            TestResource.Program.InvocationOverride = null;
            await ((IHost)secondHost).DisposeAsync();
            if (firstHost is not null)
            {
                await firstHost.DisposeAsync();
            }
        }
    }

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
            ResourceEntryExitException exception = await Should.ThrowAsync<ResourceEntryExitException>(
                () => invocation.Completion.WaitAsync(cancellationTokenSource.Token));

            // Assert
            options.RunMode.ShouldBe(ResourceHostRunMode.InProcess);
            exception.ExitCode.ShouldBe(75);
            exception.InnerException.ShouldBeOfType<InvalidOperationException>()
                .Message.ShouldBe("fixture stop failure");
            System.Environment.ExitCode.ShouldBe(23);
            output.ToString().ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(originalOutput);
            System.Environment.ExitCode = originalExitCode;
        }
    }

    private static async Task DirectEntryPoint(string[] args)
    {
        DirectEntryInvocationState state = DirectEntryInvocation.Value
            ?? throw new InvalidOperationException("The direct entry invocation state was not installed.");
        state.Entered.TrySetResult(true);
        await state.Continue.Task.ConfigureAwait(false);

        state.Context = ResourceRuntime.Current;
        state.Arguments = args;
        state.Host = new TestHost(new TestHostOptions());
        IResourceControlPlane controlPlane = ResourceControlPlane.Create();
        if (state.ControlPlaneLookupAssembly is not null)
        {
            state.ControlPlaneResolved = ResourceRuntime.TryCreateControlPlane(
                state.ControlPlaneLookupAssembly,
                out IResourceControlPlane? resolved);
            controlPlane = resolved
                ?? throw new InvalidOperationException(
                    "The invoked resource control plane was not resolved from its logical assembly.");
        }
        ResourceRuntime.HostBuilt(state.Host, controlPlane);
    }

    private sealed class DirectEntryAssembly : Assembly
    {
        private static readonly MethodInfo EntryMethod = typeof(ResourceRuntimeEntryInvocationTests)
            .GetMethod(nameof(DirectEntryPoint), BindingFlags.NonPublic | BindingFlags.Static)!;

        public override MethodInfo? EntryPoint => EntryMethod;

        public override AssemblyName GetName() => new(nameof(DirectEntryAssembly));

        public override AssemblyName GetName(bool copiedName) => GetName();
    }

    private sealed class DirectEntryInvocationState
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Continue { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal string[]? Arguments { get; set; }

        internal ResourceContext? Context { get; set; }

        internal Assembly? ControlPlaneLookupAssembly { get; init; }

        internal bool ControlPlaneResolved { get; set; }

        internal IHost? Host { get; set; }
    }
}
