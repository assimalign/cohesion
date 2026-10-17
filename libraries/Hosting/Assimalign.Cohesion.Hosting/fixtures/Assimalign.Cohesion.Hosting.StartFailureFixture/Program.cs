using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.StartFailureFixture;

internal static class Program
{
    // Keep the driver on the main thread: neither the assertion nor cleanup may need
    // the sole pool worker that the old reporting invocation monopolizes.
    private static int Main()
    {
        try
        {
            ThreadPool.GetMinThreads(out _, out int minimumIo);
            ThreadPool.GetMaxThreads(out _, out int maximumIo);
            Require(ThreadPool.SetMinThreads(1, minimumIo), "Could not set the worker minimum.");
            Require(ThreadPool.SetMaxThreads(1, maximumIo), "Could not set the worker maximum.");
            ThreadPool.GetMaxThreads(out int workers, out _);
            Require(workers == 1, "The fixture must have exactly one pool worker.");

            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var exited = new ManualResetEventSlim();
            var expectedFailure = new InvalidOperationException("startup failed");
            var host = new BlockingReportHost(expectedFailure, entered, release, exited);
            IHost lifetime = host;
            Task start = lifetime.StartAsync(CancellationToken.None);
            bool completedBeforeRelease;
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(5)), "The reporting hook never entered.");
                // A wait handle does not need a timer/pool continuation in the driver.
                // The allowance exceeds the one-second budget, but the hook stays blocked
                // until completion has been sampled, never merely for a fixed race window.
                completedBeforeRelease = ((IAsyncResult)start).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3));
                Require(!exited.IsSet, "The reporting hook must still be blocked at the observation.");
            }
            finally
            {
                release.Set();
                Require(exited.Wait(TimeSpan.FromSeconds(5)), "The reporting thread did not exit.");
            }

            try
            {
                start.GetAwaiter().GetResult();
                throw new InvalidOperationException("Startup unexpectedly succeeded.");
            }
            catch (InvalidOperationException failure) when (ReferenceEquals(failure, expectedFailure))
            {
            }
            lifetime.DisposeAsync().AsTask().GetAwaiter().GetResult();

            Require(completedBeforeRelease, "StartAsync exceeded its reporting budget while the hook held the sole pool worker.");
            Require(host.Reports == 1, "The hook must run exactly once.");
            Require(!host.ReportedOnPool, "Reporting must use a dedicated thread.");
            Require(expectedFailure.Data["Assimalign.Cohesion.Hosting.StartFailureHookException"] is TimeoutException,
                "The original failure must retain the reporting timeout.");
            Require(host.Context.State == HostState.Failed, "Rollback must precede reporting.");
            Console.WriteLine("workers=1; reports=1; completed-before-release=true; dedicated-thread=true");
            return 0;
        }
        catch (Exception failure)
        {
            // The parent test asserts this process's exit code and includes this evidence.
            Console.Error.WriteLine(failure);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class BlockingReportHost : Host<FailureContext>
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private readonly ManualResetEventSlim _exited;

        public BlockingReportHost(Exception failure, ManualResetEventSlim entered, ManualResetEventSlim release,
            ManualResetEventSlim exited) : base(new FailureOptions())
        {
            _entered = entered;
            _release = release;
            _exited = exited;
            Context = new FailureContext(failure);
        }

        public override FailureContext Context { get; }
        public int Reports { get; private set; }
        public bool ReportedOnPool { get; private set; }

        protected override Task OnStartFailedAsync(Exception exception, CancellationToken cancellationToken = default)
        {
            Reports++;
            ReportedOnPool = Thread.CurrentThread.IsThreadPoolThread;
            _entered.Set();
            try { _release.Wait(); }
            finally { _exited.Set(); }
            return Task.CompletedTask;
        }
    }

    private sealed class FailureOptions : HostOptions<FailureContext>
    {
    }

    private sealed class FailureContext : HostContext
    {
        public FailureContext(Exception failure)
        {
            HostedServices = [new FailingService(failure)];
        }

        public override IHostEnvironment Environment { get; } = new HostEnvironment("Test");
        public override IEnumerable<IHostService> HostedServices { get; }
    }

    private sealed class FailingService : IHostService
    {
        private readonly Exception _failure;

        public FailingService(Exception failure)
        {
            _failure = failure;
        }

        public ServiceId Id { get; } = ServiceId.New();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.FromException(_failure);
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
