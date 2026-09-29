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
    Justification = "Tests invoke a compiler-rooted fixture entry point retained by a direct method reference.")]
public class ResourceCredentialVerifierTests
{
    private const string displayPrefix = "Cohesion Test [Hosting] - Resource credential verifier: ";
    private static readonly AsyncLocal<EntryInvocationState?> _entryInvocation = new();

    [Fact(DisplayName = displayPrefix + "Registration: A second registration for one assembly is refused")]
    public void RegisterCredentialVerifier_WhenAssemblyAlreadyRegistered_Throws()
    {
        // Arrange
        var assembly = new TestResourceAssembly("DuplicateCredentialRegistration");
        ResourceRuntime.RegisterCredentialVerifier(assembly, static context => new RecordingCredentialVerifier(context));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ResourceRuntime.RegisterCredentialVerifier(assembly, static context => new RecordingCredentialVerifier(context)));

        // Assert
        exception.Message.ShouldBe(
            "Resource assembly 'DuplicateCredentialRegistration' already registered a credential verifier.");
    }

    [Fact(DisplayName = displayPrefix + "Registration: Null arguments are refused")]
    public void RegisterCredentialVerifier_WhenArgumentIsNull_Throws()
    {
        // Arrange
        var assembly = new TestResourceAssembly("NullCredentialRegistration");

        // Act
        Action nullAssembly = () => ResourceRuntime.RegisterCredentialVerifier(null!, static context => new RecordingCredentialVerifier(context));
        Action nullFactory = () => ResourceRuntime.RegisterCredentialVerifier(assembly, null!);
        Action nullContext = () => ResourceRuntime.TryGetCredentialVerifier(null!, out _);

        // Assert
        Should.Throw<ArgumentNullException>(nullAssembly);
        Should.Throw<ArgumentNullException>(nullFactory);
        Should.Throw<ArgumentNullException>(nullContext);
    }

    [Fact(DisplayName = displayPrefix + "Create: Each call creates a verifier for the current context")]
    public void TryCreateCredentialVerifier_ForRegisteredAssembly_CreatesInstancesForCurrentContext()
    {
        // Arrange
        var assembly = new TestResourceAssembly("CreateCredentialVerifier");
        ResourceRuntime.RegisterCredentialVerifier(assembly, static context => new RecordingCredentialVerifier(context));
        var context = new ResourceContext(resourceName: "create");
        using IDisposable scope = ResourceRuntime.CreateScope(context);

        // Act
        bool created = ResourceRuntime.TryCreateCredentialVerifier(assembly, out IResourceCredentialVerifier? first);
        ResourceRuntime.TryCreateCredentialVerifier(assembly, out IResourceCredentialVerifier? second).ShouldBeTrue();

        // Assert
        created.ShouldBeTrue();
        first.ShouldBeOfType<RecordingCredentialVerifier>().Context.ShouldBeSameAs(context);
        second.ShouldNotBeSameAs(first);
        ResourceRuntime.TryCreateCredentialVerifier(
                new TestResourceAssembly("UnregisteredCredentialVerifier"),
                out IResourceCredentialVerifier? missing)
            .ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "Create: A factory that returns null is refused")]
    public void TryCreateCredentialVerifier_WhenFactoryReturnsNull_Throws()
    {
        // Arrange
        var assembly = new TestResourceAssembly("NullCredentialVerifier");
        ResourceRuntime.RegisterCredentialVerifier(assembly, static _ => null!);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ResourceRuntime.TryCreateCredentialVerifier(assembly, out _));

        // Assert
        exception.Message.ShouldBe("Resource assembly 'NullCredentialVerifier' returned a null credential verifier.");
    }

    [Fact(DisplayName = displayPrefix + "Get: Each invocation context binds exactly one verifier of its resolved assembly")]
    public void TryGetCredentialVerifier_AfterControlPlaneResolution_BindsOneVerifierPerContext()
    {
        // Arrange
        var assembly = new TestResourceAssembly("ScopedCredentialVerifier");
        ResourceRuntime.RegisterCredentialVerifier(assembly, static context => new RecordingCredentialVerifier(context));
        var firstContext = new ResourceContext(resourceName: "first");
        var secondContext = new ResourceContext(resourceName: "second");
        using (ResourceRuntime.CreateScope(firstContext))
        {
            ResourceRuntime.TryCreateControlPlane(assembly, out _).ShouldBeFalse();
        }

        using (ResourceRuntime.CreateScope(secondContext))
        {
            ResourceRuntime.TryCreateControlPlane(assembly, out _).ShouldBeFalse();
        }

        // Act
        ResourceRuntime.TryGetCredentialVerifier(firstContext, out IResourceCredentialVerifier? first).ShouldBeTrue();
        ResourceRuntime.TryGetCredentialVerifier(firstContext, out IResourceCredentialVerifier? repeated).ShouldBeTrue();
        ResourceRuntime.TryGetCredentialVerifier(secondContext, out IResourceCredentialVerifier? second).ShouldBeTrue();

        // Assert
        repeated.ShouldBeSameAs(first);
        first.ShouldBeOfType<RecordingCredentialVerifier>().Context.ShouldBeSameAs(firstContext);
        second.ShouldBeOfType<RecordingCredentialVerifier>().Context.ShouldBeSameAs(secondContext);
        second.ShouldNotBeSameAs(first);
    }

    [Fact(DisplayName = displayPrefix + "Get: A context that resolved no resource assembly has no verifier")]
    public void TryGetCredentialVerifier_WhenContextResolvedNoAssembly_ReturnsFalse()
    {
        // Arrange
        var context = new ResourceContext(resourceName: "unresolved");

        // Act
        bool found = ResourceRuntime.TryGetCredentialVerifier(context, out IResourceCredentialVerifier? verifier);

        // Assert
        found.ShouldBeFalse();
        verifier.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "Get: A verifier registered after control-plane resolution is found on first use")]
    public void TryGetCredentialVerifier_WhenRegisteredAfterResolution_ReturnsVerifier()
    {
        // Arrange
        var assembly = new TestResourceAssembly("LateCredentialVerifier");
        var context = new ResourceContext(resourceName: "late");
        using (ResourceRuntime.CreateScope(context))
        {
            ResourceRuntime.TryCreateControlPlane(assembly, out _);
        }

        bool before = ResourceRuntime.TryGetCredentialVerifier(context, out _);

        // Act
        ResourceRuntime.RegisterCredentialVerifier(assembly, static context => new RecordingCredentialVerifier(context));
        bool after = ResourceRuntime.TryGetCredentialVerifier(context, out IResourceCredentialVerifier? verifier);

        // Assert
        before.ShouldBeFalse();
        after.ShouldBeTrue();
        verifier.ShouldBeOfType<RecordingCredentialVerifier>().Context.ShouldBeSameAs(context);
    }

    [Fact(DisplayName = displayPrefix + "Get: A plain application without a frame or registration never snapshots the environment")]
    public async Task TryCreateControlPlane_WithoutFrameOrRegistration_DoesNotMaterializeCurrent()
    {
        // Arrange
        var assembly = new TestResourceAssembly("PlainApplicationCredentialVerifier");
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "bootstrap.jwt");
        string? prior = Environment.GetEnvironmentVariable(AppEnvironment.Variables.BootstrapTokenPath);
        Environment.SetEnvironmentVariable(AppEnvironment.Variables.BootstrapTokenPath, missing);
        try
        {
            Task<bool> plain;
            Task<ResourceContext> snapshot;
            using (ExecutionContext.SuppressFlow())
            {
                // Suppressed flow gives each task an empty ambient frame, like a fresh process.
                plain = Task.Run(() => ResourceRuntime.TryCreateControlPlane(assembly, out _));
                snapshot = Task.Run(() => ResourceRuntime.Current);
            }

            // Act
            bool created = await plain;
            Exception? snapshotFailure = await Record.ExceptionAsync(() => snapshot);

            // Assert
            created.ShouldBeFalse();
            snapshotFailure.ShouldNotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppEnvironment.Variables.BootstrapTokenPath, prior);
        }
    }

    [Fact(DisplayName = displayPrefix + "Keying: An in-process entry invocation resolves its own assembly's verifier")]
    public async Task TryCreateCredentialVerifier_InsideEntryInvocation_PrefersInvokedAssembly()
    {
        // Arrange
        MethodInfo entryPoint = typeof(ResourceCredentialVerifierTests)
            .GetMethod(nameof(EntryPoint), BindingFlags.NonPublic | BindingFlags.Static)!;
        var member = new TestResourceAssembly("InProcessCredentialMember", entryPoint);
        var processEntry = new TestResourceAssembly("InProcessCredentialGateway");
        ResourceRuntime.RegisterCredentialVerifier(member, static context => new RecordingCredentialVerifier(context));
        ResourceRuntime.RegisterCredentialVerifier(processEntry, static _ => throw new InvalidOperationException("The gateway's verifier must not be used."));
        var context = new ResourceContext(resourceName: "member");
        var state = new EntryInvocationState { LookupAssembly = processEntry };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _entryInvocation.Value = state;

        try
        {
            using IDisposable scope = ResourceRuntime.CreateScope(context);

            // Act
            IResourceEntryInvocation invocation = ResourceRuntime.InvokeEntryPoint(member, []);
            await invocation.HostReady.WaitAsync(cancellation.Token);
            await invocation.Completion.WaitAsync(cancellation.Token);

            // Assert
            state.Created.ShouldBeOfType<RecordingCredentialVerifier>().Context.ShouldBeSameAs(context);
            state.Bound.ShouldNotBeNull();
            ResourceRuntime.TryGetCredentialVerifier(context, out IResourceCredentialVerifier? bound).ShouldBeTrue();
            bound.ShouldBeSameAs(state.Bound);
        }
        finally
        {
            _entryInvocation.Value = null;
            if (state.Host is not null)
            {
                await state.Host.DisposeAsync();
            }
        }
    }

    [Theory(DisplayName = displayPrefix + "Presentation: An authorization value splits at its first space")]
    [InlineData(null, "", "")]
    [InlineData("", "", "")]
    [InlineData("Bearer", "Bearer", "")]
    [InlineData("Bearer abc.def", "Bearer", "abc.def")]
    [InlineData("bearer  padded ", "bearer", " padded ")]
    [InlineData("Token a b", "Token", "a b")]
    public void FromAuthorizationValue_WithValue_SplitsSchemeAndCredential(
        string? authorization,
        string scheme,
        string credential)
    {
        // Arrange
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        // Act
        ResourceCredentialPresentation presentation = ResourceCredentialPresentation.FromAuthorizationValue(
            authorization,
            "resource",
            now);

        // Assert
        presentation.Scheme.ShouldBe(scheme);
        presentation.Credential.ShouldBe(credential);
        presentation.ExpectedAudience.ShouldBe("resource");
        presentation.Now.ShouldBe(now);
        presentation.ClientCertificate.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "Verification: The default value falls through as NoResult")]
    public void Default_Verification_IsNoResult()
    {
        // Arrange
        ResourceCredentialVerification verification = default;

        // Act
        ResourceCredentialStatus status = verification.Status;

        // Assert
        status.ShouldBe(ResourceCredentialStatus.NoResult);
        verification.Caller.ShouldBeNull();
        verification.Failure.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "Presentation: ToString redacts the presented credential")]
    public void ToString_WithPresentedCredential_ShouldRedactTheCredential()
    {
        // Arrange
        ResourceCredentialPresentation presentation = ResourceCredentialPresentation.FromAuthorizationValue(
            "Bearer eyJ-secret-token", "api", DateTimeOffset.UnixEpoch);

        // Act
        string text = presentation.ToString();

        // Assert
        text.ShouldContain("Scheme = Bearer", Case.Sensitive);
        text.ShouldContain("Credential = <redacted>", Case.Sensitive);
        text.ShouldContain("ExpectedAudience = api", Case.Sensitive);
        text.ShouldNotContain("eyJ-secret-token", Case.Sensitive);
    }

    private static Task EntryPoint(string[] args)
    {
        EntryInvocationState state = _entryInvocation.Value
            ?? throw new InvalidOperationException("The entry invocation state was not installed.");

        // An in-process member runs beneath the gateway's process entry assembly; the invocation frame's
        // assembly must win, exactly as it does for the default control plane.
        ResourceRuntime.TryCreateCredentialVerifier(state.LookupAssembly!, out IResourceCredentialVerifier? created);
        state.Created = created;
        ResourceRuntime.TryCreateControlPlane(state.LookupAssembly!, out _);
        ResourceRuntime.TryGetCredentialVerifier(ResourceRuntime.Current, out IResourceCredentialVerifier? bound);
        state.Bound = bound;

        state.Host = new TestHost(new TestHostOptions());
        ResourceRuntime.HostBuilt(state.Host, ResourceControlPlane.Create());
        return Task.CompletedTask;
    }

    private sealed class EntryInvocationState
    {
        internal Assembly? LookupAssembly { get; init; }

        internal IResourceCredentialVerifier? Created { get; set; }

        internal IResourceCredentialVerifier? Bound { get; set; }

        internal IHost? Host { get; set; }
    }
}
