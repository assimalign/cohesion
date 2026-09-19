using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.FileSystem.Physical.Tests;

/// <summary>
/// Verifies caller-owned native watcher disposal and concurrent notification cleanup.
/// </summary>
public class PhysicalFileSystemWatchTests
{
    [Theory(DisplayName = "Cohesion Test [PhysicalFileSystem] - Watch: Token and provider disposal are safe in either order")]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_TokenAndProviderInEitherOrder_ShouldBeIdempotent(bool tokenFirst)
    {
        using var fixture = new Fixture();
        var token = fixture.FileSystem.Watch(Glob.Parse("**"));
        using var disposable = (IDisposable)token;
        using var changed = token.OnChange(_ => { }, null);
        using var created = token.OnCreate<object>(_ => { }, null);
        using var deleted = token.OnDelete<object>(_ => { }, null);
        using var renamed = token.OnRename<object>(_ => { }, null);

        Should.NotThrow(() =>
        {
            if (tokenFirst)
            {
                disposable.Dispose();
                fixture.FileSystem.CreateFile("still-usable.txt");
                fixture.FileSystem.Exists("still-usable.txt").ShouldBeTrue();
            }

            fixture.FileSystem.Dispose();
            disposable.Dispose();
            fixture.FileSystem.Dispose();
            disposable.Dispose();
            changed.Dispose();
            created.Dispose();
            deleted.Dispose();
            renamed.Dispose();
        });

        Should.Throw<ObjectDisposedException>(() => token.OnCreate<object>(_ => { }, null));
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Watch: A callback can dispose its own token")]
    public async Task Dispose_FromCallback_ShouldCompleteWithoutSubscriberMutationFailure()
    {
        using var fixture = new Fixture();
        var token = fixture.FileSystem.Watch(Glob.Parse("**"));
        using var disposable = (IDisposable)token;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = token.OnCreate<object>(_ =>
        {
            var exception = Record.Exception(disposable.Dispose);
            if (exception is null)
            {
                disposed.TrySetResult();
            }
            else
            {
                disposed.TrySetException(exception);
            }
        }, null);
        using var otherSubscription = token.OnCreate<object>(_ => { }, null);

        fixture.FileSystem.CreateFile("trigger.txt");

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Should.Throw<ObjectDisposedException>(() => token.OnCreate<object>(_ => { }, null));
        fixture.FileSystem.CreateFile("after-disposal.txt");
        fixture.FileSystem.Exists("after-disposal.txt").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Watch: Removing a registration from a callback preserves notification")]
    public async Task OnCreate_UnsubscribeFromCallback_ShouldNotifyRemainingSubscriber()
    {
        using var fixture = new Fixture();
        var token = fixture.FileSystem.Watch(Glob.Parse("**"));
        using var disposable = (IDisposable)token;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? first = null;
        first = token.OnCreate<object>(_ => first!.Dispose(), null);
        using var firstSubscription = first;
        using var secondSubscription = token.OnCreate<object>(_ => notified.TrySetResult(), null);

        fixture.FileSystem.CreateFile("trigger.txt");

        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact(DisplayName = "Cohesion Test [PhysicalFileSystem] - Watch: Disposal is safe while a callback is in flight")]
    public async Task Dispose_WithCallbackInFlight_ShouldReleaseWatcherWithoutWaitingForUserCode()
    {
        using var fixture = new Fixture();
        var token = fixture.FileSystem.Watch(Glob.Parse("**"));
        using var disposable = (IDisposable)token;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = token.OnCreate<object>(_ =>
        {
            entered.TrySetResult();
            finished.TrySetResult(release.Wait(TimeSpan.FromSeconds(10)));
        }, null);

        fixture.FileSystem.CreateFile("trigger.txt");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            await Task.Run(disposable.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
            fixture.FileSystem.CreateFile("after-disposal.txt");
            fixture.FileSystem.Exists("after-disposal.txt").ShouldBeTrue();
        }
        finally
        {
            release.Set();
            (await finished.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CohesionPhysicalWatchTests", Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            FileSystem = new PhysicalFileSystem(_root);
        }

        public PhysicalFileSystem FileSystem { get; }

        public void Dispose()
        {
            FileSystem.Dispose();
            Directory.Delete(_root, true);
        }
    }
}
