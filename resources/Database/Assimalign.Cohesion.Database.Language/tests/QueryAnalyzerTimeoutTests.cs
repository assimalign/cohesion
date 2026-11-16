using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

[Collection("Analyzer timeout faults")]
public sealed class QueryAnalyzerTimeoutTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Late faults are observed after a wait or startup failure (#1264, #1267)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Parse_LateAnalyzerFault_ShouldObserveExceptionAfterWaitOrStartupFailure(int failureMode)
    {
        // Arrange
        var failure = new InvalidOperationException("late-analyzer-test");
        int unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(exception => ReferenceEquals(exception, failure)))
            {
                Interlocked.Increment(ref unobserved);
                args.SetObserved();
            }
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            // Act: the helper drops all strong references before finalizers run.
            var task = TimeoutThenFault(failure, failureMode);
            var elapsed = Stopwatch.StartNew();
            int collections = 0;
            do
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                collections++;
            }
            while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(100) || task.IsAlive)
                && elapsed.Elapsed < TimeSpan.FromSeconds(30));

            // Assert
            collections.ShouldBeGreaterThan(0);
            task.IsAlive.ShouldBeFalse();
            Volatile.Read(ref unobserved).ShouldBe(0);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference TimeoutThenFault(Exception failure, int failureMode)
    {
        var pending = new TaskCompletionSource();
        var options = new QueryParserOptions { AnalyzerTimeout = failureMode == 2 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100) };
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, token) =>
        {
            if (failureMode == 1)
            {
                // Wait for recorded cancellation before the next analyzer starts; 30 s is a hang guard.
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            }
            return pending.Task;
        }));
        var startupFailure = new InvalidOperationException();
        if (failureMode != 0)
        {
            options.Analyzers.Add(new DelegateQueryAnalyzer((_, _) =>
                failureMode == 2 ? throw startupFailure : Task.CompletedTask));
        }
        var parser = new AnalyzerTestParser(options);
        if (failureMode == 2)
        {
            Should.Throw<InvalidOperationException>(() => parser.Parse("")).ShouldBeSameAs(startupFailure);
        }
        else
        {
            Should.Throw<OperationCanceledException>(() => parser.Parse(""));
        }
        pending.SetException(failure);
        return new WeakReference(pending.Task);
    }
}
