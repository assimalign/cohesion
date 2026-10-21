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
    [Fact(DisplayName = "Cohesion Test [Database.Language] - A late analyzer fault after timeout is observed (#1267, #1422)")]
    public void Parse_LateAnalyzerFault_ShouldObserveExceptionAfterTimeout()
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
            var task = TimeoutThenFault(failure);
            var elapsed = Stopwatch.StartNew();
            int collections = 0;
            do
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                collections++;
            }
            while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(100) || task.IsAlive)
                && elapsed.Elapsed < TimeSpan.FromSeconds(10));

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
    private static WeakReference TimeoutThenFault(Exception failure)
    {
        var pending = new TaskCompletionSource();
        var options = new QueryParserOptions { AnalyzerTimeout = TimeSpan.FromMilliseconds(20) };
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, _) => pending.Task));
        var parser = new AnalyzerTestParser(options);
        Should.Throw<OperationCanceledException>(() => parser.Parse(""));
        pending.SetException(failure);
        return new WeakReference(pending.Task);
    }
}
