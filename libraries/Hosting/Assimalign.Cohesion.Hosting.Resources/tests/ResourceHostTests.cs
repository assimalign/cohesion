using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

[Collection(nameof(SerialCollection))]
public class ResourceHostTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ResourceHost: ";

    [Fact(DisplayName = DisplayPrefix + "RunAsync: Emits protocol lines in lifecycle order")]
    public async Task RunAsync_WithResourceOptions_EmitsProtocolLinesInLifecycleOrder()
    {
        // Arrange
        var protocolLines = new List<string>();
        var exitCodes = new List<int>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalSource = new TestResourceHostSignalSource();
        var hostOptions = new TestHostOptions
        {
            ShutdownTimeout = TimeSpan.FromMinutes(1),
        };
        hostOptions.HostedServices.Add(new DelegateHostService(
            static _ => Task.CompletedTask,
            _ =>
            {
                protocolLines.ShouldBe(new[]
                {
                    "cohesion-resource: ready",
                    "cohesion-resource: stopping",
                });
                return Task.CompletedTask;
            }));
        string contentRootPath = Path.GetFullPath("resource-content");
        hostOptions.ContentRootPath = FileSystemPath.Parse(contentRootPath);
        var host = new TestHost(hostOptions);

        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            contentRootPath: contentRootPath,
            stopEventName: string.Empty,
            protocolLineWriter: line =>
            {
                protocolLines.Add(line);
                if (line == ResourceHost.ReadyProtocolLine)
                {
                    ready.TrySetResult();
                }
            },
            exitCodeHandler: exitCodes.Add,
            signalSource: signalSource));

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        Task run = host.RunAsync(cancellationTokenSource.Token);
        await ready.Task.WaitAsync(cancellationTokenSource.Token);
        signalSource.Signal(ResourceHostStopSignal.Terminate).ShouldBeTrue();
        await run.WaitAsync(cancellationTokenSource.Token);

        // Assert
        protocolLines.ShouldBe(new[]
        {
            "cohesion-resource: ready",
            "cohesion-resource: stopping",
            "cohesion-resource: stopped",
        });
        exitCodes.ShouldBe(new[] { 0 });
        hostOptions.ShutdownTimeout.ShouldBe(TimeSpan.FromSeconds(25));
        host.Context.Environment.ContentRootPath.ShouldBe(FileSystemPath.Parse(contentRootPath));
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown: Ignores a signal before Started")]
    public async Task Shutdown_BeforeHostIsStarted_IsIgnored()
    {
        // Arrange
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalSource = new TestResourceHostSignalSource();
        var hostOptions = new TestHostOptions();

        hostOptions.HostedServices.Add(new DelegateHostService(async cancellationToken =>
        {
            startEntered.TrySetResult();
            await releaseStart.Task.WaitAsync(cancellationToken);
        }));

        var host = new TestHost(hostOptions);
        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            stopEventName: string.Empty,
            protocolLineWriter: line =>
            {
                if (line == ResourceHost.ReadyProtocolLine)
                {
                    ready.TrySetResult();
                }
            },
            exitCodeHandler: static _ => { },
            signalSource: signalSource));

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task run = host.RunAsync(cancellationTokenSource.Token);
        await startEntered.Task.WaitAsync(cancellationTokenSource.Token);

        // Act - this signal arrives while the service is still starting.
        signalSource.Signal(ResourceHostStopSignal.Interrupt).ShouldBeFalse();

        // Assert - startup continues and the run remains parked after reaching Started.
        host.Context.State.ShouldBe(HostState.Starting);
        releaseStart.TrySetResult();
        await ready.Task.WaitAsync(cancellationTokenSource.Token);
        run.IsCompleted.ShouldBeFalse();

        signalSource.Signal(ResourceHostStopSignal.Terminate).ShouldBeTrue();
        await run.WaitAsync(cancellationTokenSource.Token);
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "RunAsync: Keeps plain host behavior without registration")]
    public async Task RunAsync_WithoutResourceOptions_KeepsPlainHostBehavior()
    {
        // Arrange
        var hostOptions = new TestHostOptions
        {
            ShutdownTimeout = TimeSpan.FromSeconds(17),
        };
        hostOptions.HostedServices.Add(new DelegateHostService(
            static _ => throw new InvalidOperationException("plain start failure")));
        var host = new TestHost(hostOptions);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.RunAsync());

        // Assert
        exception.Message.ShouldBe("plain start failure");
        hostOptions.ShutdownTimeout.ShouldBe(TimeSpan.FromSeconds(17));
    }

    [Fact(DisplayName = DisplayPrefix + "RunAsync: In-process mode avoids process effects")]
    public async Task RunAsync_WithInProcessMode_AvoidsProcessEffects()
    {
        // Arrange
        var protocolLines = new List<string>();
        var exitCodes = new List<int>();
        var signalSource = new TestResourceHostSignalSource();
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            stopEventName: string.Empty,
            protocolLineWriter: protocolLines.Add,
            exitCodeHandler: exitCodes.Add,
            signalSource: signalSource,
            runMode: ResourceHostRunMode.InProcess));

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        Task run = host.RunAsync(cancellationTokenSource.Token);
        while (host.Context.State is not HostState.Started)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationTokenSource.Token);
        }

        host.Context.Shutdown();
        await run.WaitAsync(cancellationTokenSource.Token);

        // Assert
        protocolLines.ShouldBeEmpty();
        exitCodes.ShouldBeEmpty();
        signalSource.SubscriptionCount.ShouldBe(0);
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Content root: Rejects a mismatch before host start")]
    public async Task RunAsync_WhenContentRootDoesNotMatch_ThrowsBeforeStart()
    {
        // Arrange
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            contentRootPath: Path.GetFullPath("different-resource-content"),
            runMode: ResourceHostRunMode.InProcess));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.RunAsync());

        // Assert
        exception.Message.ShouldBe(
            "The host content root must match the ambient resource content root.");
        host.Context.State.ShouldBe(HostState.Idle);
    }

    [Fact(DisplayName = DisplayPrefix + "Exit codes: Converts a typed startup failure at the run boundary")]
    public async Task RunAsync_WithTypedStartupFailure_ReportsConfigurationExitCode()
    {
        // Arrange
        var protocolLines = new List<string>();
        var exitCodes = new List<int>();
        var hostOptions = new TestHostOptions();
        hostOptions.HostedServices.Add(new DelegateHostService(
            static _ => throw new TestResourceConfigurationException()));
        var host = new TestHost(hostOptions);
        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            stopEventName: string.Empty,
            protocolLineWriter: protocolLines.Add,
            exceptionClassifier: ResourceHostOptions.CreateExceptionClassifier<
                TestResourceConfigurationException,
                TestResourceDependencyException>(),
            exitCodeHandler: exitCodes.Add,
            signalSource: new TestResourceHostSignalSource()));

        // Act
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        exitCodes.ShouldBe(new[] { 64 });
        protocolLines.ShouldBeEmpty();
        host.Context.State.ShouldBe(HostState.Failed);
    }

    [Fact(DisplayName = DisplayPrefix + "Protocol lines: A writer failure drains the host and reports runtime failure")]
    public async Task RunAsync_WhenProtocolWriterFails_DrainsAndReportsRuntimeExitCode()
    {
        // Arrange
        var exitCodes = new List<int>();
        var host = new TestHost(new TestHostOptions());
        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            stopEventName: string.Empty,
            protocolLineWriter: static _ => throw new IOException("stdout unavailable"),
            exitCodeHandler: exitCodes.Add,
            signalSource: new TestResourceHostSignalSource()));

        // Act
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        exitCodes.ShouldBe(new[] { 75 });
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Exit codes: A cancelled drain reports termination")]
    public async Task RunAsync_WhenDirectStopDrainIsCancelled_ReportsTerminatedExitCode()
    {
        // Arrange
        var protocolLines = new List<string>();
        var exitCodes = new List<int>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostOptions = new TestHostOptions();
        hostOptions.HostedServices.Add(new DelegateHostService(
            static _ => Task.CompletedTask,
            static cancellationToken => Task.FromCanceled(cancellationToken)));
        var host = new TestHost(hostOptions);
        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            stopEventName: string.Empty,
            protocolLineWriter: line =>
            {
                protocolLines.Add(line);
                if (line == ResourceHost.ReadyProtocolLine)
                {
                    ready.TrySetResult();
                }
            },
            exitCodeHandler: exitCodes.Add,
            signalSource: new TestResourceHostSignalSource()));

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await ready.Task.WaitAsync(timeoutSource.Token);
        using var cancelledStopSource = new CancellationTokenSource();
        cancelledStopSource.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(
            () => ((IHost)host).StopAsync(cancelledStopSource.Token));
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        exitCodes.ShouldBe(new[] { 143 });
        protocolLines.ShouldBe(new[]
        {
            "cohesion-resource: ready",
            "cohesion-resource: stopping",
            "cohesion-resource: stopped",
        });
    }

    [Fact(DisplayName = DisplayPrefix + "Protocol lines: Ready is not emitted after a concurrent stop")]
    public async Task RunAsync_WhenStopWinsDuringStartedHook_DoesNotEmitReadyAfterStopped()
    {
        // Arrange
        var protocolLines = new List<string>();
        var exitCodes = new List<int>();
        var startedHookEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartedHook = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hostOptions = new TestHostOptions();
        var host = new DelayedStartedTestHost(
            hostOptions,
            startedHookEntered,
            releaseStartedHook);
        host.Context.Runner = new ResourceHostRunner(new ResourceHostOptions(
            stopEventName: string.Empty,
            protocolLineWriter: protocolLines.Add,
            exitCodeHandler: exitCodes.Add,
            signalSource: new TestResourceHostSignalSource()));

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = host.RunAsync(timeoutSource.Token);
        await startedHookEntered.Task.WaitAsync(timeoutSource.Token);

        // Act
        await ((IHost)host).StopAsync(timeoutSource.Token);
        releaseStartedHook.TrySetResult();
        await run.WaitAsync(timeoutSource.Token);

        // Assert
        protocolLines.ShouldBe(new[]
        {
            "cohesion-resource: stopping",
            "cohesion-resource: stopped",
        });
        exitCodes.ShouldBe(new[] { 0 });
    }

    [Fact(DisplayName = DisplayPrefix + "Exit codes: Maps sysexits v1 failures")]
    public void ClassifyExitCode_WithTypedAndLifecycleFailures_MapsSysexitsV1()
    {
        // Arrange
        var options = new ResourceHostOptions(
            stopEventName: string.Empty,
            exceptionClassifier: ResourceHostOptions.CreateExceptionClassifier<
                TestResourceConfigurationException,
                TestResourceDependencyException>(),
            exitCodeHandler: static _ => { },
            signalSource: new TestResourceHostSignalSource());

        // Act
        int configuration = ResourceHost.ClassifyExitCode(
            new TestResourceConfigurationException(),
            options,
            hasReachedReady: true,
            isDrainAborted: false,
            ResourceHostStopSignal.None);
        int dependency = ResourceHost.ClassifyExitCode(
            new TestResourceDependencyException(),
            options,
            hasReachedReady: false,
            isDrainAborted: false,
            ResourceHostStopSignal.None);
        int startup = ResourceHost.ClassifyExitCode(
            new InvalidOperationException(),
            options,
            hasReachedReady: false,
            isDrainAborted: false,
            ResourceHostStopSignal.None);
        int runtime = ResourceHost.ClassifyExitCode(
            new InvalidOperationException(),
            options,
            hasReachedReady: true,
            isDrainAborted: false,
            ResourceHostStopSignal.None);
        int interruptedDrain = ResourceHost.ClassifyExitCode(
            new OperationCanceledException(),
            options,
            hasReachedReady: true,
            isDrainAborted: true,
            ResourceHostStopSignal.Interrupt);
        int terminatedDrain = ResourceHost.ClassifyExitCode(
            new OperationCanceledException(),
            options,
            hasReachedReady: true,
            isDrainAborted: true,
            ResourceHostStopSignal.Terminate);
        int unrelatedCancellation = ResourceHost.ClassifyExitCode(
            new OperationCanceledException(),
            options,
            hasReachedReady: true,
            isDrainAborted: false,
            ResourceHostStopSignal.Terminate);

        // Assert
        configuration.ShouldBe(64);
        dependency.ShouldBe(69);
        startup.ShouldBe(70);
        runtime.ShouldBe(75);
        interruptedDrain.ShouldBe(130);
        terminatedDrain.ShouldBe(143);
        unrelatedCancellation.ShouldBe(75);
    }

    [Fact(DisplayName = DisplayPrefix + "Exit codes: Classifies wrapped startup failures")]
    public void ClassifyExitCode_WithWrappedStartupFailures_PreservesTypedMappings()
    {
        // Arrange
        var options = new ResourceHostOptions(
            stopEventName: string.Empty,
            exceptionClassifier: ResourceHostOptions.CreateExceptionClassifier<
                TestResourceConfigurationException,
                TestResourceDependencyException>(),
            exitCodeHandler: static _ => { },
            signalSource: new TestResourceHostSignalSource());

        var configurationFailure = new HostStartupException(
            "The resource host failed to start.",
            new TestResourceConfigurationException());
        var dependencyFailure = new HostStartupException(
            "The resource host failed to start.",
            new TestResourceDependencyException());
        var genericFailure = new HostStartupException(
            "The resource host failed to start.",
            new InvalidOperationException());

        // Act
        int configuration = ResourceHost.ClassifyExitCode(
            configurationFailure,
            options,
            hasReachedReady: false,
            isDrainAborted: false,
            ResourceHostStopSignal.None);
        int dependency = ResourceHost.ClassifyExitCode(
            dependencyFailure,
            options,
            hasReachedReady: false,
            isDrainAborted: false,
            ResourceHostStopSignal.None);
        int startup = ResourceHost.ClassifyExitCode(
            genericFailure,
            options,
            hasReachedReady: false,
            isDrainAborted: false,
            ResourceHostStopSignal.None);

        // Assert
        configuration.ShouldBe(64);
        dependency.ShouldBe(69);
        startup.ShouldBe(70);
    }

    [Fact(DisplayName = DisplayPrefix + "ShutdownTimeout: Derives default and floor from stop grace")]
    public void ShutdownTimeout_WithDefaultAndShortGrace_DerivesExpectedBudget()
    {
        // Arrange & Act
        TimeSpan defaultTimeout = new ResourceHostOptions(
            stopEventName: string.Empty,
            exitCodeHandler: static _ => { },
            signalSource: new TestResourceHostSignalSource()).ShutdownTimeout;
        TimeSpan floorAtFive = ResourceHostOptions.DeriveShutdownTimeout(5);
        TimeSpan floorAtNine = ResourceHostOptions.DeriveShutdownTimeout(9);
        TimeSpan aboveFloor = ResourceHostOptions.DeriveShutdownTimeout(11);

        // Assert
        defaultTimeout.ShouldBe(TimeSpan.FromSeconds(25));
        floorAtFive.ShouldBe(TimeSpan.FromSeconds(5));
        floorAtNine.ShouldBe(TimeSpan.FromSeconds(5));
        aboveFloor.ShouldBe(TimeSpan.FromSeconds(6));
        Should.Throw<ArgumentOutOfRangeException>(() => ResourceHostOptions.DeriveShutdownTimeout(4));
    }

    [Fact(DisplayName = DisplayPrefix + "Content root: Rejects relative contract values")]
    public void ResolveContentRootPath_WithRelativeValue_ThrowsArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ResourceHostOptions.ResolveContentRootPath("relative-content"));

        // Assert
        exception.ParamName.ShouldBe("contentRootPath");
    }

    [Fact(DisplayName = DisplayPrefix + "Content root: Uses contract value then application base fallback")]
    public void ResolveContentRootPath_WithAndWithoutEnvironment_UsesContractThenFallback()
    {
        // Arrange
        string? originalContentRoot = System.Environment.GetEnvironmentVariable(
            ResourceEnvironment.ContentRoot,
            EnvironmentVariableTarget.Process);
        string configuredContentRoot = Path.GetFullPath("configured-resource-content");

        try
        {
            // Act
            System.Environment.SetEnvironmentVariable(
                ResourceEnvironment.ContentRoot,
                configuredContentRoot,
                EnvironmentVariableTarget.Process);
            FileSystemPath configured = ResourceHostOptions.ResolveContentRootPath();

            System.Environment.SetEnvironmentVariable(
                ResourceEnvironment.ContentRoot,
                value: null,
                EnvironmentVariableTarget.Process);
            FileSystemPath fallback = ResourceHostOptions.ResolveContentRootPath();

            // Assert
            configured.ShouldBe(FileSystemPath.Parse(configuredContentRoot));
            fallback.ShouldBe(FileSystemPath.Parse(Path.GetFullPath(AppContext.BaseDirectory)));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(
                ResourceEnvironment.ContentRoot,
                originalContentRoot,
                EnvironmentVariableTarget.Process);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Signals: Registers safely on the current operating system")]
    public void Subscribe_OnCurrentOperatingSystem_DoesNotThrow()
    {
        // Arrange
        string? stopEventName = OperatingSystem.IsWindows()
            ? $@"Local\cohesion-{Guid.NewGuid():N}-stop"
            : null;
        using EventWaitHandle? gatewayEvent = OperatingSystem.IsWindows()
            ? new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                stopEventName)
            : null;

        // Act & Assert
        Should.NotThrow(() =>
        {
            using IDisposable subscription = ResourceHostSignalSource.Instance.Subscribe(
                static _ => false,
                stopEventName);
        });
    }

    [Fact(DisplayName = DisplayPrefix + "Named event: Requests a Windows resource stop")]
    public async Task Subscribe_WhenWindowsNamedEventIsSet_RequestsResourceStop()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string stopEventName = $@"Local\cohesion-{Guid.NewGuid():N}-stop";
        var stopSignal = new TaskCompletionSource<ResourceHostStopSignal>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var gatewayEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            stopEventName);
        using IDisposable subscription = ResourceHostSignalSource.Instance.Subscribe(
            signal => stopSignal.TrySetResult(signal),
            stopEventName);
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        gatewayEvent.Set();
        ResourceHostStopSignal observed = await stopSignal.Task.WaitAsync(cancellationTokenSource.Token);

        // Assert
        observed.ShouldBe(ResourceHostStopSignal.Terminate);
    }

    private sealed class DelayedStartedTestHost : Host<TestHostContext>
    {
        private readonly TaskCompletionSource _startedHookEntered;
        private readonly TaskCompletionSource _releaseStartedHook;

        internal DelayedStartedTestHost(
            TestHostOptions options,
            TaskCompletionSource startedHookEntered,
            TaskCompletionSource releaseStartedHook)
            : base(options)
        {
            _startedHookEntered = startedHookEntered;
            _releaseStartedHook = releaseStartedHook;
            Context = new TestHostContext(options.HostedServices, options.ContentRootPath);
        }

        public override TestHostContext Context { get; }

        protected override async Task OnStartedAsync(CancellationToken cancellationToken = default)
        {
            _startedHookEntered.TrySetResult();
            await _releaseStartedHook.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TestResourceConfigurationException : Exception
    {
    }

    private sealed class TestResourceDependencyException : Exception
    {
    }
}
