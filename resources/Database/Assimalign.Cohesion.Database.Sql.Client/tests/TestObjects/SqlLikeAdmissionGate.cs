using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Holds a row-time matcher until the server has observed a disconnected client.</summary>
internal sealed class SqlLikeAdmissionGate : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;
    private readonly Action? _previous;
    private int _entries;

    internal SqlLikeAdmissionGate(DatabaseServerSession session)
    {
        _registration = LifetimeSource(session).Token.Register(() => _disconnected.TrySetResult());
        _previous = MatchHook(null);
        MatchHook(null) = () =>
        {
            Interlocked.Increment(ref _entries);
            _started.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(20))) { throw new TimeoutException("Matcher was not released."); }
        };
    }

    internal Task Started => _started.Task;
    internal Task Disconnected => _disconnected.Task;
    internal int Entries => Volatile.Read(ref _entries);
    internal void Release() => _release.Set();

    // Test-only, AOT-compatible access avoids granting a foreign test assembly the
    // engine's internals or exposing production API solely to synchronize a test.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "BeforeLikeMatch")]
    private static extern ref Action? MatchHook(
        [UnsafeAccessorType("Assimalign.Cohesion.Database.Sql.Internal.SqlExpressionEvaluator, Assimalign.Cohesion.Database.Sql")] object? evaluator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lifetimeSource")]
    private static extern ref CancellationTokenSource LifetimeSource(
        [UnsafeAccessorType("Assimalign.Cohesion.Database.Sql.Internal.SqlDatabaseServerSession, Assimalign.Cohesion.Database.Sql")] object session);

    public void Dispose()
    {
        MatchHook(null) = _previous;
        _release.Set();
        _registration.Dispose();
        _release.Dispose();
    }
}
