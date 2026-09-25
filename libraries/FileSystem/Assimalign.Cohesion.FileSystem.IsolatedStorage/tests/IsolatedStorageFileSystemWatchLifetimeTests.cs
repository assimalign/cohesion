using System;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.FileSystem.Internal;

namespace Assimalign.Cohesion.FileSystem.IsolatedStorage.Tests;

/// <summary>
/// Verifies watcher ownership and disposal with controlled timer callbacks and real storage.
/// </summary>
public sealed class IsolatedStorageFileSystemWatchLifetimeTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Dispose: Watch token stops its timer while its owner remains usable")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Dispose_WatchToken_ShouldStopPollingWithoutDisposingOwner(int watchScope)
    {
        var timeProvider = new ManualWatchTimeProvider();
        using var fileSystem = CreateFileSystem(timeProvider);
        var file = fileSystem.CreateFile("watched/item.bin");
        var token = watchScope switch
        {
            0 => fileSystem.Watch(null),
            1 => fileSystem.GetDirectory("watched").Watch(null),
            _ => file.Watch(),
        };
        var timer = timeProvider.Timers.Single();
        int deletions = 0;
        using var registration = token.OnDelete<object>(_ => deletions++, null);

        fileSystem.DeleteFile("watched/item.bin");
        timer.Fire().ShouldBeTrue();
        deletions.ShouldBe(1);
        fileSystem.CreateFile("watched/item.bin");

        ((IDisposable)token).Dispose();

        timer.IsDisposed.ShouldBeTrue();
        timer.Fire().ShouldBeFalse();
        fileSystem.DeleteFile("watched/item.bin");
        timer.InvokeQueuedCallback();
        deletions.ShouldBe(1);
        fileSystem.CreateFile("still-usable.bin");
        fileSystem.Exists("still-usable.bin").ShouldBeTrue();

        var replacement = fileSystem.Watch(null);
        using var replacementLifetime = (IDisposable)replacement;
        int creations = 0;
        using var replacementRegistration = replacement.OnCreate<object>(_ => creations++, null);
        fileSystem.CreateFile("replacement-watch.bin");
        timeProvider.Timers.Last().Fire().ShouldBeTrue();
        creations.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Dispose: Token and owner disposal are safe in either order and repeatedly")]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_TokenAndOwnerInEitherOrder_ShouldStopEachOwnedTimerOnce(bool tokenFirst)
    {
        var timeProvider = new ManualWatchTimeProvider();
        using var fileSystem = CreateFileSystem(timeProvider);
        var file = fileSystem.CreateFile("owned.bin");
        var directoryToken = fileSystem.Watch(null);
        var fileToken = file.Watch();

        Should.NotThrow(() =>
        {
            if (tokenFirst)
            {
                ((IDisposable)directoryToken).Dispose();
                ((IDisposable)directoryToken).Dispose();
            }
            fileSystem.Dispose();
            fileSystem.Dispose();
            ((IDisposable)directoryToken).Dispose();
            ((IDisposable)directoryToken).Dispose();
            ((IDisposable)fileToken).Dispose();
            ((IDisposable)fileToken).Dispose();
        });

        timeProvider.Timers.Count.ShouldBe(2);
        foreach (var timer in timeProvider.Timers)
        {
            timer.IsDisposed.ShouldBeTrue();
            timer.DisposeCalls.ShouldBe(1);
            timer.Fire().ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Dispose: In-flight callbacks cannot block disposal or deliver later subscribers")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_CallbackInFlight_ShouldCompleteWithoutWaitingForUserCode(bool disposeOwner)
    {
        var timeProvider = new ManualWatchTimeProvider();
        using var fileSystem = CreateFileSystem(timeProvider);
        var token = fileSystem.Watch(null);
        var timer = timeProvider.Timers.Single();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int laterCallbacks = 0;
        using var firstRegistration = token.OnCreate<object>(_ =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        }, null);
        using var laterRegistration = token.OnCreate<object>(_ => Interlocked.Increment(ref laterCallbacks), null);
        fileSystem.CreateFile("in-flight.bin");
        var firing = Task.Run(timer.Fire);

        try
        {
            await callbackStarted.Task.WaitAsync(_testTimeout);
            await Task.Run(() =>
            {
                if (disposeOwner)
                {
                    fileSystem.Dispose();
                }
                else
                {
                    ((IDisposable)token).Dispose();
                }
            }).WaitAsync(_testTimeout);
            timer.IsDisposed.ShouldBeTrue();
            if (!disposeOwner)
            {
                fileSystem.CreateFile("usable-during-callback.bin");
                fileSystem.Exists("usable-during-callback.bin").ShouldBeTrue();
            }
        }
        finally
        {
            releaseCallback.TrySetResult();
            await firing.WaitAsync(_testTimeout);
        }

        laterCallbacks.ShouldBe(0);
        timer.Fire().ShouldBeFalse();
        Should.NotThrow(timer.InvokeQueuedCallback);
        laterCallbacks.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Dispose: A callback can dispose its token or owner without deadlock")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_FromCallback_ShouldStopPollingAndSkipRemainingSubscribers(bool disposeOwner)
    {
        var timeProvider = new ManualWatchTimeProvider();
        using var fileSystem = CreateFileSystem(timeProvider);
        var token = fileSystem.Watch(null);
        var timer = timeProvider.Timers.Single();
        int callbacks = 0;
        int laterCallbacks = 0;
        using var registration = token.OnCreate<object>(_ =>
        {
            callbacks++;
            if (disposeOwner)
            {
                fileSystem.Dispose();
            }
            else
            {
                ((IDisposable)token).Dispose();
            }
        }, null);
        using var laterRegistration = token.OnCreate<object>(_ => laterCallbacks++, null);
        fileSystem.CreateFile("self-dispose.bin");

        (await Task.Run(timer.Fire).WaitAsync(_testTimeout)).ShouldBeTrue();

        callbacks.ShouldBe(1);
        laterCallbacks.ShouldBe(0);
        timer.IsDisposed.ShouldBeTrue();
        timer.Fire().ShouldBeFalse();
        Should.NotThrow(timer.InvokeQueuedCallback);
        callbacks.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Dispose: Queued callbacks and later registrations are harmless after owner shutdown")]
    public void Dispose_OwnerBeforeQueuedCallback_ShouldIgnoreCallbackAndLaterRegistrations()
    {
        var timeProvider = new ManualWatchTimeProvider();
        using var fileSystem = CreateFileSystem(timeProvider);
        var token = fileSystem.Watch(null);
        var timer = timeProvider.Timers.Single();
        int callbacks = 0;
        using var registration = token.OnCreate<object>(_ => callbacks++, null);
        fileSystem.CreateFile("queued.bin");

        fileSystem.Dispose();
        using var lateRegistration = token.OnCreate<object>(_ => callbacks++, null);

        Should.NotThrow(timer.InvokeQueuedCallback);
        callbacks.ShouldBe(0);
        timer.IsDisposed.ShouldBeTrue();
        timer.Fire().ShouldBeFalse();
        Should.NotThrow(() => ((IDisposable)token).Dispose());
    }

    [Fact(DisplayName = "Cohesion Test [IsolatedStorageFileSystem] - Watch: Snapshot handles allow concurrent deletion")]
    public void DeleteFile_ActiveWatcherSnapshotHandle_ShouldAllowDeletion()
    {
        IsolatedStorageFileSystemTestFixture.ClearUserStoreForAssembly();
        using var storage = IsolatedStorageFile.GetUserStoreForAssembly();
        using (storage.CreateFile("delete-during-snapshot.bin"))
        {
        }
        var timeProvider = new ManualWatchTimeProvider();
        using var token = IsolatedStorageFileSystemPollingEventToken.ForFile(
            storage, "/delete-during-snapshot.bin", TimeSpan.FromMilliseconds(50), timeProvider, _ => { });
        var timer = timeProvider.Timers.Single();
        int deletions = 0;
        using var registration = token.OnDelete<object>(_ => deletions++, null);

        // Keep the exact stream factory used by polling open throughout the competing delete.
        using (var snapshotStream = IsolatedStorageFileSystemPollingEventToken.OpenSnapshotStream(
            storage, "delete-during-snapshot.bin"))
        {
            snapshotStream.CanRead.ShouldBeTrue();
            timer.IsDisposed.ShouldBeFalse();
            Should.NotThrow(() => storage.DeleteFile("delete-during-snapshot.bin"));
        }

        storage.FileExists("delete-during-snapshot.bin").ShouldBeFalse();
        timer.Fire().ShouldBeTrue();
        deletions.ShouldBe(1);
    }

    private static IsolatedStorageFileSystem CreateFileSystem(ManualWatchTimeProvider timeProvider)
    {
        IsolatedStorageFileSystemTestFixture.ClearUserStoreForAssembly();
        return new IsolatedStorageFileSystem(new IsolatedStorageFileSystemOptions
        {
            WatchPollInterval = TimeSpan.FromMilliseconds(50),
        }, timeProvider);
    }
}
