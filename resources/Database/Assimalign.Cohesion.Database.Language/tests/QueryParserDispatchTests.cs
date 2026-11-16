using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Language.Tests;

[Collection("Analyzer timeout faults")]
public sealed class QueryParserDispatchTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Completed synchronous overruns preserve every analyzer in either order")]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_SynchronousAnalyzerOverrun_ShouldStartEveryAnalyzer(bool overrunFirst)
    {
        // Arrange
        int diagnosticStarts = 0;
        var diagnostic = new Diagnostic("TEST", "analyzed", 0, 0, DiagnosticSeverity.Warning);
        var overrun = new DelegateQueryAnalyzer((_, token) =>
        {
            // Recorded timer cancellation decides progress; 30 s only guards against a hang.
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            return Task.CompletedTask;
        });
        var reporter = new DelegateQueryAnalyzer((context, _) =>
        {
            Interlocked.Increment(ref diagnosticStarts);
            context.Statement.AddDiagnostic(diagnostic);
            return Task.CompletedTask;
        });
        var options = new QueryParserOptions { AnalyzerTimeout = TimeSpan.FromMilliseconds(50) };
        options.Analyzers.Add(overrunFirst ? overrun : reporter);
        options.Analyzers.Add(overrunFirst ? reporter : overrun);
        var parser = new AnalyzerTestParser(options);

        // Act
        var statement = parser.Parse("source");

        // Assert
        statement.ShouldBeOfType<AnalyzerTestStatement>();
        statement.Diagnostics.ShouldHaveSingleItem().ShouldBeSameAs(diagnostic);
        diagnosticStarts.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Language] - Startup failure cancels earlier analyzers and preserves its original exception")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parse_AnalyzerStartupFailure_ShouldCancelEarlierTasks(bool callbackThrows)
    {
        // Arrange
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException();
        var callbackFailure = new InvalidOperationException();
        CancellationTokenRegistration registration = default;
        var options = new QueryParserOptions { AnalyzerTimeout = TimeSpan.FromSeconds(30) };
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, token) =>
        {
            registration = token.Register(() =>
            {
                pending.TrySetCanceled(token);
                canceled.TrySetResult();
                if (callbackThrows) { throw callbackFailure; }
            });
            return pending.Task;
        }));
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, _) => throw failure));

        try
        {
            // Act / Assert
            Should.Throw<InvalidOperationException>(() => new AnalyzerTestParser(options).Parse("source"))
                .ShouldBeSameAs(failure);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            pending.Task.IsCanceled.ShouldBeTrue();
        }
        finally
        {
            registration.Dispose();
            pending.TrySetCanceled();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Parser serialization includes analyzer completion")]
    public async Task Parse_ConcurrentCall_ShouldWaitForEarlierAnalyzerCompletion()
    {
        // Arrange
        var order = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCalling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new QueryParserOptions { AnalyzerTimeout = TimeSpan.FromSeconds(30) };
        int starts = 0;
        options.Analyzers.Add(new DelegateQueryAnalyzer(async (_, _) =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                order.Enqueue("analyze:first");
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
                order.Enqueue("analyzed:first");
            }
        }));
        var parser = new GatedTestParser(options, order);
        var first = Task.Factory.StartNew(() => parser.Parse("first"), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var second = new Thread(() =>
        {
            try
            {
                order.Enqueue("call:second");
                secondCalling.SetResult();
                parser.Parse("second");
                secondFinished.SetResult();
            }
            catch (Exception exception)
            {
                // Transfer failures from the dedicated test thread to its awaited task.
                secondFinished.SetException(exception);
            }
        }) { IsBackground = true };

        try
        {
            // Act
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            second.Start();
            await secondCalling.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            // The thread either contends on the monitor or has entered the second core.
            SpinWait.SpinUntil(() => (second.ThreadState & ThreadState.WaitSleepJoin) != 0
                || secondFinished.Task.IsCompleted || order.Contains("parse:second"), TimeSpan.FromSeconds(30))
                .ShouldBeTrue();

            // Assert: recorded progress, rather than a scheduling delay, pins the gate.
            order.ShouldBe(["parse:first", "analyze:first", "call:second"]);
            release.SetResult();
            await Task.WhenAll(first, secondFinished.Task).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            order.ShouldBe(["parse:first", "analyze:first", "call:second", "analyzed:first", "parse:second"]);
            starts.ShouldBe(2);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if ((second.ThreadState & ThreadState.Unstarted) == 0)
            {
                second.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            }
        }
    }
}
