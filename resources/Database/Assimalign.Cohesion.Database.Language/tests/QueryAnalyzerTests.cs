using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Language.Tests;

public sealed class QueryAnalyzerTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Language] - No analyzers means no timeout or analyzer allocations (#1267, #1422)")]
    [InlineData(0)]
    [InlineData(5000)]
    public void Parse_NoAnalyzers_ShouldAllocateNothingAndIgnoreTimeout(int milliseconds)
    {
        // Arrange: isolate analyzer work from the model's lexer and statement allocations.
        var parser = new AllocationAnalyzerTestParser(new QueryParserOptions { AnalyzerTimeout = TimeSpan.FromMilliseconds(milliseconds) });
        for (int i = 0; i < 100; i++) { parser.Parse(""); }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { parser.Parse(""); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Parse waits for every analyzer's diagnostics")]
    public void Parse_AsynchronousAnalyzers_ShouldReturnAllDiagnostics()
    {
        // Arrange
        var options = new QueryParserOptions();
        foreach (int position in new[] { 2, 1 })
        {
            options.Analyzers.Add(new DelegateQueryAnalyzer(async (context, token) =>
            {
                await Task.Delay(1, token).ConfigureAwait(false);
                context.Statement.AddDiagnostic(new Diagnostic("TEST", "analyzed", position, position, DiagnosticSeverity.Warning));
            }));
        }
        var parser = new AnalyzerTestParser(options);

        // Act
        var statement = parser.Parse("");

        // Assert
        statement.Diagnostics.Select(diagnostic => diagnostic.End).ShouldBe(new int?[] { 1, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Parse propagates analyzer failures")]
    public void Parse_FaultedAnalyzer_ShouldPropagateOriginalException()
    {
        // Arrange
        var failure = new InvalidOperationException();
        var options = new QueryParserOptions();
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, _) => Task.FromException(failure)));
        var parser = new AnalyzerTestParser(options);

        // Act
        var actual = Should.Throw<InvalidOperationException>(() => parser.Parse(""));

        // Assert
        actual.ShouldBeSameAs(failure);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Parse propagates analyzer cancellation")]
    public void Parse_CanceledAnalyzer_ShouldPropagateCancellation()
    {
        // Arrange
        var token = new CancellationToken(canceled: true);
        var options = new QueryParserOptions();
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, _) => Task.FromCanceled(token)));
        var parser = new AnalyzerTestParser(options);

        // Act
        var actual = Should.Throw<OperationCanceledException>(() => parser.Parse(""));

        // Assert
        actual.CancellationToken.ShouldBe(token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Parse times out an analyzer that ignores cancellation")]
    public void Parse_UnresponsiveAnalyzer_ShouldCancelWait()
    {
        // Arrange
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new QueryParserOptions { AnalyzerTimeout = TimeSpan.FromMilliseconds(50) };
        CancellationToken analyzerToken = default;
        options.Analyzers.Add(new DelegateQueryAnalyzer((_, token) =>
        {
            analyzerToken = token;
            return pending.Task;
        }));
        var parser = new AnalyzerTestParser(options);

        // Act
        try
        {
            var actual = Should.Throw<OperationCanceledException>(() => parser.Parse(""));

            // Assert
            actual.CancellationToken.ShouldBe(analyzerToken);
            analyzerToken.IsCancellationRequested.ShouldBeTrue();
            pending.Task.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            pending.TrySetResult();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - AddDiagnostic rejects null without changing diagnostics")]
    public void AddDiagnostic_NullDiagnostic_ShouldThrowArgumentNullException()
    {
        // Arrange
        var statement = new AnalyzerTestStatement();

        // Act
        var actual = Should.Throw<ArgumentNullException>(() => statement.AddDiagnostic(null!));

        // Assert
        actual.ParamName.ShouldBe("diagnostic");
        statement.Diagnostics.ShouldBeEmpty();
    }
}
