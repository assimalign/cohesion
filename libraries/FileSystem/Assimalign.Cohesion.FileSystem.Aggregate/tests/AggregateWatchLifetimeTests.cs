using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.FileSystem.Aggregate.Tests;

/// <summary>
/// Verifies aggregate watch-token ownership and disposal during callback delivery.
/// </summary>
public class AggregateWatchLifetimeTests
{
    [Fact(DisplayName = "Cohesion Test [AggregateWatch] - Dispose: releases owned child tokens and leaves borrowed providers and unrelated tokens alive")]
    public void Dispose_Token_ShouldDisposeOnlyItsChildTokens()
    {
        using var provider = new TrackedProvider();
        using var aggregate = new AggregateFileSystemBuilder().Mount("/data", provider).Build();
        var unrelated = provider.Watch(Glob.Parse("/**"));
        using var unrelatedLifetime = (IDisposable)unrelated;
        int unrelatedCallbacks = 0;
        using var unrelatedRegistration = unrelated.OnCreate<object?>(_ => unrelatedCallbacks++, null);
        var token = aggregate.Watch(null);
        var child = provider.Tokens[1];
        int callbacks = 0;
        using var registration = token.OnCreate<object?>(_ => callbacks++, null);
        aggregate.CreateFile("/data/before.txt");
        callbacks.ShouldBe(1);

        ((IDisposable)token).Dispose();
        ((IDisposable)token).Dispose();
        using var lateRegistration = token.OnCreate<object?>(_ => callbacks++, null);
        provider.CreateFile("/after.txt");

        child.DisposeCount.ShouldBe(1);
        provider.Tokens[0].DisposeCount.ShouldBe(0);
        provider.DisposeCount.ShouldBe(0);
        callbacks.ShouldBe(1);
        unrelatedCallbacks.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [AggregateWatch] - Dispose: owner cleans abandoned tokens and both orders are idempotent")]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Dispose_EitherOrder_ShouldCleanOwnedTokens(bool tokenFirst, bool asyncDispose)
    {
        using var provider = new TrackedProvider();
        var aggregate = new AggregateFileSystemBuilder().Mount("/data", provider).Build();
        var token = aggregate.Watch(null);
        int callbacks = 0;
        using var registration = token.OnCreate<object?>(_ => callbacks++, null);
        if (tokenFirst) { ((IDisposable)token).Dispose(); }

        if (asyncDispose) { await aggregate.DisposeAsync(); }
        else { aggregate.Dispose(); }
        aggregate.Dispose();
        await aggregate.DisposeAsync();
        ((IDisposable)token).Dispose();
        ((IDisposable)token).Dispose();
        provider.CreateFile("/after.txt");

        provider.Tokens[0].DisposeCount.ShouldBe(1);
        provider.DisposeCount.ShouldBe(0);
        callbacks.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [AggregateWatch] - Dispose: in-flight callbacks finish without reviving a token")]
    public async Task Dispose_DuringCallback_ShouldStopRemainingCallbacks()
    {
        using var provider = new TrackedProvider();
        using var aggregate = new AggregateFileSystemBuilder().Mount("/data", provider).Build();
        var token = aggregate.Watch(null);
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
        var mutation = Task.Run(() => provider.CreateFile("/during.txt"));
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            lifetime.Dispose();
        }
        finally { release.Set(); }
        await mutation.WaitAsync(TimeSpan.FromSeconds(5));

        laterCallbacks.ShouldBe(0);
        provider.Tokens[0].DisposeCount.ShouldBe(1);
    }

    private sealed class TrackedProvider : IFileSystem
    {
        private readonly InMemoryFileSystem _inner = new(new InMemoryFileSystemOptions());
        public List<TrackedToken> Tokens { get; } = new();
        public int DisposeCount { get; private set; }
        public string Name => _inner.Name;
        public bool IsReadOnly => _inner.IsReadOnly;
        public Size Size => _inner.Size;
        public Size SpaceAvailable => _inner.SpaceAvailable;
        public Size SpaceUsed => _inner.SpaceUsed;
        public IFileSystemDirectory RootDirectory => _inner.RootDirectory;
        public bool Exists(FileSystemPath path) => _inner.Exists(path);
        public IFileSystemDirectory GetDirectory(FileSystemPath path) => _inner.GetDirectory(path);
        public IFileSystemFile GetFile(FileSystemPath path) => _inner.GetFile(path);
        public IFileSystemInfo GetInfo(FileSystemPath path) => _inner.GetInfo(path);
        public IFileSystemDirectory CreateDirectory(FileSystemPath path) => _inner.CreateDirectory(path);
        public IFileSystemFile CreateFile(FileSystemPath path) => _inner.CreateFile(path);
        public void DeleteDirectory(FileSystemPath path) => _inner.DeleteDirectory(path);
        public void DeleteFile(FileSystemPath path) => _inner.DeleteFile(path);
        public void CopyFile(FileSystemPath source, FileSystemPath destination) => _inner.CopyFile(source, destination);
        public void Move(FileSystemPath source, FileSystemPath destination) => _inner.Move(source, destination);
        public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null) => _inner.EnumerateFileSystem(options);
        public IEnumerator<IFileSystemInfo> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IFileSystemEventToken Watch(Glob? pattern)
        {
            var token = new TrackedToken(_inner.Watch(pattern));
            Tokens.Add(token);
            return token;
        }
        public void Dispose() { DisposeCount++; _inner.Dispose(); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class TrackedToken(IFileSystemEventToken inner) : IFileSystemEventToken, IDisposable
    {
        public int DisposeCount { get; private set; }
        public IDisposable OnChange(Action<object?> callback, object? state) => inner.OnChange(callback, state);
        public IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state) => inner.OnChange(callback, state);
        public IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state) => inner.OnCreate(callback, state);
        public IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state) => inner.OnDelete(callback, state);
        public IDisposable OnRename<T>(Action<FileSystemRenameEvent<T?>> callback, T? state) => inner.OnRename(callback, state);
        public void Dispose() { DisposeCount++; ((IDisposable)inner).Dispose(); }
    }
}
