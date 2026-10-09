using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Security.Internal;

namespace Assimalign.Cohesion.Database.Security.Tests;

/// <summary>
/// Serializes the tests that observe the security event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(DatabaseSecurityEventSourceCollection), DisableParallelization = true)]
public class DatabaseSecurityEventSourceCollection
{
}

/// <summary>
/// The security child root's event source against the repository's EventSource convention: one
/// event per authentication verdict or failure, never the evidence, and no cost while nobody listens.
/// </summary>
[Collection(nameof(DatabaseSecurityEventSourceCollection))]
public sealed class DatabaseSecurityEventSourceTests
{
    private readonly ITestOutputHelper _output;

    public DatabaseSecurityEventSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - DatabaseSecurityEventSource: Should be named for its assembly")]
    public void GetName_DatabaseSecurityEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(DatabaseSecurityEventSource));

        // Assert
        name.ShouldBe(typeof(DatabaseSecurityEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Security");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - DatabaseSecurityEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(DatabaseSecurityEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Security", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - DatabaseSecurityEventSource: Should report each verdict and failure once with its declared payload")]
    public async Task AuthenticateAsync_Verdicts_ShouldReportEachOnce()
    {
        // Arrange
        string database = "db-" + Guid.NewGuid().ToString("N");
        var failure = new InvalidOperationException("the directory is unreachable");
        using var recorder = new SecurityEventRecorder(EventLevel.Verbose);

        // Act
        bool accepted = await new RecordingAuthenticator(result: true).AuthenticateAsync(database, "ada", new byte[] { 1, 2, 3 });
        bool rejected = await new RecordingAuthenticator(result: false).AuthenticateAsync(database, "eve", new byte[] { 4, 5, 6 });
        bool deferred = await new DeferredAuthenticator(result: true).AuthenticateAsync(database, "lin", ReadOnlyMemory<byte>.Empty);
        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new FaultedAuthenticator(failure).AuthenticateAsync(database, "mal", ReadOnlyMemory<byte>.Empty));

        // Assert: the outcomes are unchanged, and the failure reaches the caller as it was thrown.
        accepted.ShouldBeTrue();
        rejected.ShouldBeFalse();
        deferred.ShouldBeTrue();
        thrown.ShouldBeSameAs(failure);

        var events = recorder.Events.Where(e => Equals(e.Payload?[1], database)).ToArray();
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        events.Select(e => (e.EventName, Principal: (string)e.Payload![2]!)).ShouldBe(
        [
            ("AuthenticationSucceeded", "ada"),
            ("AuthenticationRejected", "eve"),
            ("AuthenticationSucceeded", "lin"),
            ("AuthenticationFailed", "mal"),
        ]);

        events[0].EventId.ShouldBe(1);
        events[0].Level.ShouldBe(EventLevel.Verbose);
        events[0].PayloadNames.ShouldBe(["authenticator", "database", "principal"]);
        events[0].Payload.ShouldBe([nameof(RecordingAuthenticator), database, "ada"]);

        events[1].EventId.ShouldBe(2);
        events[1].Level.ShouldBe(EventLevel.Verbose);
        events[1].PayloadNames.ShouldBe(["authenticator", "database", "principal"]);

        events[2].Payload![0].ShouldBe(nameof(DeferredAuthenticator));

        events[3].EventId.ShouldBe(3);
        events[3].Level.ShouldBe(EventLevel.Error);
        events[3].PayloadNames.ShouldBe(["authenticator", "database", "principal", "exceptionType", "exceptionMessage"]);
        events[3].Payload.ShouldBe([nameof(FaultedAuthenticator), database, "mal", typeof(InvalidOperationException).FullName, "the directory is unreachable"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - DatabaseSecurityEventSource: A core that throws synchronously still throws from the call itself, once reported")]
    public void AuthenticateAsync_CoreThrowsSynchronously_ShouldThrowFromTheCall()
    {
        // Arrange
        string database = "db-" + Guid.NewGuid().ToString("N");
        var failure = new InvalidOperationException("thrown before any task exists");
        var authenticator = new SynchronouslyThrowingAuthenticator(failure);
        using var recorder = new SecurityEventRecorder(EventLevel.Verbose);

        // Act: the call itself throws, as it does while nobody listens; no task is returned.
        var thrown = Should.Throw<InvalidOperationException>(() => authenticator.AuthenticateAsync(database, "ada", ReadOnlyMemory<byte>.Empty));

        // Assert
        thrown.ShouldBeSameAs(failure);
        recorder.Events.Where(e => Equals(e.Payload?[1], database)).Select(e => e.EventName).ShouldBe(["AuthenticationFailed"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - DatabaseSecurityEventSource: A canceled authentication is not a failure")]
    public async Task AuthenticateAsync_CoreCanceled_ShouldNotReportFailure()
    {
        // Arrange
        string database = "db-" + Guid.NewGuid().ToString("N");
        using var recorder = new SecurityEventRecorder(EventLevel.Verbose);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await new FaultedAuthenticator(new OperationCanceledException()).AuthenticateAsync(database, "ada", ReadOnlyMemory<byte>.Empty));

        // Assert
        recorder.Events.Where(e => Equals(e.Payload?[1], database)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - DatabaseSecurityEventSource: AuthenticateAsync allocates nothing while nobody listens")]
    public async Task AuthenticateAsync_NoListener_ShouldNotAllocate()
    {
        // Arrange: the built-in authenticator's core completes synchronously without allocating, so
        // any allocation would be the trace's.
        const int attempts = 1_000;
        DatabaseSecurityEventSource.Log.IsEnabled().ShouldBeFalse("A listener from another test is still attached.");
        DatabaseAuthenticator authenticator = DatabaseAuthenticator.AllowAll;
        for (int index = 0; index < 100; index++)
        {
            (await authenticator.AuthenticateAsync("app", "ada", ReadOnlyMemory<byte>.Empty)).ShouldBeTrue();
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < attempts; index++)
        {
            _ = await authenticator.AuthenticateAsync("app", "ada", ReadOnlyMemory<byte>.Empty);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"AuthenticateAsync, {attempts} attempts, no listener: {allocated} bytes.");

        // Assert
        allocated.ShouldBe(0L);
    }

    /// <summary>
    /// An authenticator whose verdict completes asynchronously.
    /// </summary>
    private sealed class DeferredAuthenticator : DatabaseAuthenticator
    {
        private readonly bool _result;

        public DeferredAuthenticator(bool result)
        {
            _result = result;
        }

        protected override async ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return _result;
        }
    }

    /// <summary>
    /// An authenticator whose core fails through its task.
    /// </summary>
    private sealed class FaultedAuthenticator : DatabaseAuthenticator
    {
        private readonly Exception _failure;

        public FaultedAuthenticator(Exception failure)
        {
            _failure = failure;
        }

        protected override async ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw _failure;
        }
    }

    /// <summary>
    /// An authenticator whose core throws before it returns a task.
    /// </summary>
    private sealed class SynchronouslyThrowingAuthenticator : DatabaseAuthenticator
    {
        private readonly Exception _failure;

        public SynchronouslyThrowingAuthenticator(Exception failure)
        {
            _failure = failure;
        }

        protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
            => throw _failure;
    }

    /// <summary>
    /// Records the events the security event source writes.
    /// </summary>
    private sealed class SecurityEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public SecurityEventRecorder(EventLevel level)
        {
            EnableEvents(DatabaseSecurityEventSource.Log, level, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, DatabaseSecurityEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }
}
