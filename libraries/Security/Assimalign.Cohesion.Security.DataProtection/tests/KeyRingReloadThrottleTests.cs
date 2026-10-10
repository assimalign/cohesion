using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

public class KeyRingReloadThrottleTests
{
    // [version:1][keyId:16][nonce:12][tag:16] with an empty ciphertext.
    private const int emptyPayloadLength = 45;

    private static readonly byte[] _sample = Encoding.UTF8.GetBytes("reload-sample");
    private static readonly DateTimeOffset _origin = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should read the repository once for many unknown key ids inside one interval")]
    public void Unprotect_ManyUnknownKeyIdsInsideInterval_ShouldReadRepositoryOnce()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector node = CreateNode(repository, time);
        node.Protect(_sample);
        int baseline = repository.LoadCount;

        // Act
        int rejected = 0;
        for (int i = 0; i < 50; i++)
        {
            try
            {
                node.Unprotect(Forged());
            }
            catch (DataProtectionException)
            {
                rejected++;
            }

            time.Advance(TimeSpan.FromMilliseconds(500)); // the last miss lands at +24.5s
        }

        // Assert
        rejected.ShouldBe(50);
        (repository.LoadCount - baseline).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should let an unknown key id reload again once the interval passes")]
    public void Unprotect_UnknownKeyIdOnceIntervalPasses_ShouldReadRepositoryAgain()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector node = CreateNode(repository, time);
        node.Protect(_sample);
        int baseline = repository.LoadCount;
        Should.Throw<DataProtectionException>(() => node.Unprotect(Forged()));
        time.Advance(_interval - TimeSpan.FromTicks(1));
        Should.Throw<DataProtectionException>(() => node.Unprotect(Forged()));
        int readsInsideWindow = repository.LoadCount - baseline;
        time.Advance(TimeSpan.FromTicks(1));

        // Act
        Should.Throw<DataProtectionException>(() => node.Unprotect(Forged()));

        // Assert
        readsInsideWindow.ShouldBe(1);
        (repository.LoadCount - baseline).ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should resolve a key another node wrote inside the window once the interval passes")]
    public void Unprotect_KeyWrittenByAnotherNodeInsideWindow_ShouldResolveOnceIntervalPasses()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector nodeB = CreateNode(repository, time);
        Should.Throw<DataProtectionException>(() => nodeB.Unprotect(Forged())); // opens B's window
        byte[] rotated = CreateNode(repository, time).Protect(_sample);    // node A writes a key B has not loaded
        time.Advance(TimeSpan.FromSeconds(10));
        DataProtectionException insideWindow = Should.Throw<DataProtectionException>(() => nodeB.Unprotect(rotated));
        time.Advance(_interval - TimeSpan.FromSeconds(10));

        // Act
        byte[] result = nodeB.Unprotect(rotated);

        // Assert
        insideWindow.Message.ShouldContain("unknown");
        result.ShouldBe(_sample);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should resolve a key another node wrote at once when no reload ran recently")]
    public void Unprotect_KeyWrittenByAnotherNodeWithNoRecentReload_ShouldResolveAtOnce()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector nodeB = CreateNode(repository, time);
        byte[] rotated = CreateNode(repository, time).Protect(_sample);

        // Act
        byte[] result = nodeB.Unprotect(rotated);

        // Assert
        result.ShouldBe(_sample);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should share one repository read among misses that arrive while it runs")]
    public void Unprotect_ConcurrentMissesDuringReload_ShouldShareOneRepositoryRead()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector nodeB = CreateNode(repository, time);
        byte[] rotated = CreateNode(repository, time).Protect(_sample);
        byte[][] payloads = [.. Enumerable.Range(0, 16).Select(index => index % 2 == 0 ? rotated : Forged())];
        Exception?[] outcomes = new Exception?[payloads.Length + 1];
        int baseline = repository.LoadCount;
        using ManualResetEventSlim gate = new(false);
        repository.HoldLoadsUntil(gate);
        Thread reloading = StartUnprotect(nodeB, rotated, outcomes, payloads.Length);
        Thread[] waiting = [];
        bool joined;
        try
        {
            repository.WaitForLoad(_timeout).ShouldBeTrue();
            waiting = [.. payloads.Select((payload, index) => StartUnprotect(nodeB, payload, outcomes, index))];
            SpinWait.SpinUntil(() => waiting.All(IsBlockedOrDone), _timeout).ShouldBeTrue();

            // Act
            gate.Set();
            joined = waiting.Append(reloading).All(thread => thread.Join(_timeout));
        }
        finally
        {
            gate.Set();
        }

        // Assert
        joined.ShouldBeTrue();
        (repository.LoadCount - baseline).ShouldBe(1);
        outcomes[payloads.Length].ShouldBeNull();
        for (int index = 0; index < payloads.Length; index++)
        {
            if (ReferenceEquals(payloads[index], rotated))
            {
                outcomes[index].ShouldBeNull();
            }
            else
            {
                outcomes[index].ShouldBeOfType<DataProtectionException>();
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should unprotect with a known key while a reload runs")]
    public async Task Unprotect_KnownKeyWhileReloadRuns_ShouldNotWait()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector node = CreateNode(repository, time);
        byte[] payload = node.Protect(_sample);
        using ManualResetEventSlim gate = new(false);
        repository.HoldLoadsUntil(gate);
        Thread reloading = StartUnprotect(node, Forged(), new Exception?[1], 0);
        try
        {
            repository.WaitForLoad(_timeout).ShouldBeTrue();

            // Act (a wait on the reload lock times out here)
            byte[] result = await RunOnOwnThread(() => node.Unprotect(payload)).WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            result.ShouldBe(_sample);
            reloading.IsAlive.ShouldBeTrue();
        }
        finally
        {
            gate.Set();
            reloading.Join(_timeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should protect with the active key while a reload runs")]
    public async Task Protect_WhileReloadRuns_ShouldNotWait()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector node = CreateNode(repository, time);
        node.Protect(_sample);
        using ManualResetEventSlim gate = new(false);
        repository.HoldLoadsUntil(gate);
        Thread reloading = StartUnprotect(node, Forged(), new Exception?[1], 0);
        try
        {
            repository.WaitForLoad(_timeout).ShouldBeTrue();

            // Act (a wait on the reload lock times out here)
            byte[] result = await RunOnOwnThread(() => node.Protect(_sample)).WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            node.Unprotect(result).ShouldBe(_sample);
            reloading.IsAlive.ShouldBeTrue();
        }
        finally
        {
            gate.Set();
            reloading.Join(_timeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyRing: Should not read a failing repository again inside the interval")]
    public void Unprotect_UnknownKeyIdAfterFailedReload_ShouldNotReadAgainInsideInterval()
    {
        // Arrange
        CountingKeyRepository repository = new();
        MutableTimeProvider time = new(_origin);
        IDataProtector node = CreateNode(repository, time);
        node.Protect(_sample);
        repository.FailLoadsWith(new IOException("The key repository is offline."));
        int baseline = repository.LoadCount;
        Should.Throw<IOException>(() => node.Unprotect(Forged()));

        // Act
        DataProtectionException exception = Should.Throw<DataProtectionException>(() => node.Unprotect(Forged()));

        // Assert
        exception.Message.ShouldContain("unknown");
        (repository.LoadCount - baseline).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Security.DataProtection] - Options: Should reject a non-positive unknown-key reload interval")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithNonPositiveUnknownKeyReloadInterval_ShouldThrow(int seconds)
    {
        // Arrange
        DataProtectionOptions options = new() { UnknownKeyReloadInterval = TimeSpan.FromSeconds(seconds) };

        // Act
        Action create = () => DataProtectionProvider.Create(options, new CountingKeyRepository());

        // Assert
        Should.Throw<ArgumentException>(create).ParamName.ShouldBe(nameof(DataProtectionOptions.UnknownKeyReloadInterval));
    }

    private static IDataProtector CreateNode(IKeyRepository repository, TimeProvider time)
    {
        DataProtectionOptions options = new()
        {
            ApplicationDiscriminator = "app",
            UnknownKeyReloadInterval = _interval,
        };

        return DataProtectionProvider.Create(options, repository, time).CreateProtector("purpose");
    }

    // A well-formed payload that names a key id no ring holds, as any client can send.
    private static byte[] Forged()
    {
        byte[] payload = new byte[emptyPayloadLength];
        payload[0] = 0x01;
        Guid.NewGuid().TryWriteBytes(payload.AsSpan(1, 16));
        return payload;
    }

    private static Thread StartUnprotect(IDataProtector protector, byte[] payload, Exception?[] outcomes, int index)
    {
        Thread thread = new(() =>
        {
            try
            {
                protector.Unprotect(payload);
            }
            catch (Exception exception)
            {
                outcomes[index] = exception;
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        return thread;
    }

    private static Task<byte[]> RunOnOwnThread(Func<byte[]> operation)
    {
        return Task.Factory.StartNew(operation, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static bool IsBlockedOrDone(Thread thread)
    {
        return (thread.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0;
    }
}
