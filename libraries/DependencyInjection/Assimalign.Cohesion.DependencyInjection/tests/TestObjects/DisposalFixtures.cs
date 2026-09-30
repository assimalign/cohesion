using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Services that record or fail their own disposal.
/// </summary>
internal static class DisposalFixtures
{
    internal const string DisposeFailure = "Dispose failed.";

    internal interface ISharedService
    {
    }

    internal interface IOtherSharedService
    {
    }

    internal sealed class TrackedDisposable : IDisposable, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return default;
        }
    }

    internal sealed class ThrowingDisposable : IDisposable, IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            throw new InvalidOperationException(DisposeFailure);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            throw new InvalidOperationException(DisposeFailure);
        }
    }

    /// <summary>
    /// Fails after yielding, so the scope finishes disposal on its asynchronous path.
    /// </summary>
    internal sealed class YieldingThrowingDisposable : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            IsDisposed = true;
            throw new InvalidOperationException(DisposeFailure);
        }
    }

    /// <summary>
    /// Completes after yielding, so the scope finishes disposal on its asynchronous path.
    /// </summary>
    internal sealed class YieldingDisposable : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            IsDisposed = true;
        }
    }

    internal sealed class SharedDisposable : ISharedService, IOtherSharedService, IDisposable, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return default;
        }
    }

    /// <summary>
    /// Records whether the shared instance it depends on was still undisposed when it was disposed.
    /// </summary>
    internal sealed class SharedDependent : IDisposable
    {
        private readonly SharedDisposable _shared;

        public SharedDependent(SharedDisposable shared)
        {
            _shared = shared;
        }

        public bool SharedWasLiveAtDisposal { get; private set; }

        public void Dispose() => SharedWasLiveAtDisposal = _shared.DisposeCount == 0;
    }
}
