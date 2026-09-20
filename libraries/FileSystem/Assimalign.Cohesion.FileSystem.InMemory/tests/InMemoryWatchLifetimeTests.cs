using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.FileSystem.InMemory.Tests;

/// <summary>
/// Verifies in-memory watch-token ownership, unsubscription, and callback disposal.
/// </summary>
public class InMemoryWatchLifetimeTests
{
    [Fact(DisplayName = "Cohesion Test [InMemoryWatch] - Dispose: stops callbacks while the provider stays usable")]
    public void Dispose_Token_ShouldStopCallbacksAndKeepOtherWatchersAlive()
    {
        using var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var token = fileSystem.Watch(Glob.Parse("/**"));
        var otherToken = fileSystem.Watch(Glob.Parse("/**"));
        using var otherLifetime = (IDisposable)otherToken;
        int callbacks = 0;
        int otherCallbacks = 0;
        using var registration = token.OnCreate<object?>(_ => callbacks++, null);
        using var otherRegistration = otherToken.OnCreate<object?>(_ => otherCallbacks++, null);
        fileSystem.CreateFile("/before.txt");
        callbacks.ShouldBe(1);

        ((IDisposable)token).Dispose();
        ((IDisposable)token).Dispose();
        using var lateRegistration = token.OnCreate<object?>(_ => callbacks++, null);
        fileSystem.CreateFile("/after.txt");

        callbacks.ShouldBe(1);
        otherCallbacks.ShouldBe(2);
        fileSystem.Exists("/after.txt").ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [InMemoryWatch] - Dispose: owner and token disposal are idempotent in either order")]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_EitherOrder_ShouldBeIdempotent(bool tokenFirst)
    {
        var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var directory = fileSystem.CreateDirectory("/watched");
        var file = fileSystem.CreateFile("/watched/file.txt");
        var tokens = new[] { fileSystem.Watch(Glob.Parse("/**")), directory.Watch(Glob.Parse("/**")), file.Watch() };

        Should.NotThrow(() =>
        {
            if (tokenFirst)
            {
                foreach (var token in tokens) { ((IDisposable)token).Dispose(); }
            }
            fileSystem.Dispose();
            fileSystem.Dispose();
            foreach (var token in tokens)
            {
                ((IDisposable)token).Dispose();
                ((IDisposable)token).Dispose();
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryWatch] - Dispose: an in-flight callback cannot dispatch remaining subscriptions")]
    public async Task Dispose_DuringCallback_ShouldStopRemainingCallbacks()
    {
        using var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var token = fileSystem.Watch(Glob.Parse("/**"));
        using var lifetime = (IDisposable)token;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int laterCallbacks = 0;
        using var first = token.OnCreate<object?>(_ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        }, null);
        using var second = token.OnCreate<object?>(_ => laterCallbacks++, null);
        var mutation = Task.Run(() => fileSystem.CreateFile("/during.txt"));
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            lifetime.Dispose();
        }
        finally
        {
            release.Set();
        }
        await mutation.WaitAsync(TimeSpan.FromSeconds(5));
        laterCallbacks.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryWatch] - Registration: self-disposal does not invalidate event dispatch")]
    public void OnCreate_SelfDisposal_ShouldKeepOtherRegistrationsUsable()
    {
        using var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var token = fileSystem.Watch(Glob.Parse("/**"));
        using var lifetime = (IDisposable)token;
        IDisposable? first = null;
        int callbacks = 0;
        first = token.OnCreate<object?>(_ => first!.Dispose(), null);
        using var second = token.OnCreate<object?>(_ => callbacks++, null);

        Should.NotThrow(() => fileSystem.CreateFile("/first.txt"));
        fileSystem.CreateFile("/second.txt");
        callbacks.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [InMemoryWatch] - Dispatcher: existing children use current parent subscriptions")]
    public void Watch_ExistingChildDispatcher_ShouldHonorLaterRegistrationAndDisposal()
    {
        using var fileSystem = new InMemoryFileSystem(new InMemoryFileSystemOptions());
        var file = fileSystem.CreateFile("/file.txt");
        using (var stream = file.Open(FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.WriteByte(1);
        }
        var token = fileSystem.Watch(Glob.Parse("/**"));
        using var lifetime = (IDisposable)token;
        int callbacks = 0;
        using var registration = token.OnChange<object?>(_ => callbacks++, null);
        using var writer = file.Open(FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        writer.WriteByte(2);
        callbacks.ShouldBeGreaterThan(0);
        int beforeDispose = callbacks;

        lifetime.Dispose();
        writer.WriteByte(3);
        callbacks.ShouldBe(beforeDispose);
    }
}
