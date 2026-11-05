using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>An abstract analyzer for implementing custom analysis on parsed queries.</summary>
public abstract class QueryAnalyzer
{
    /// <summary>Initializes an application-supplied query analyzer.</summary>
    protected QueryAnalyzer() { }

    /// <summary>Analyzes a parsed statement and records diagnostics on it.</summary>
    /// <param name="context">The parsed statement to analyze.</param>
    /// <param name="cancellationToken">Cancellation requested when the parser's analyzer timeout expires.</param>
    /// <returns>A task that completes when analysis and diagnostic reporting finish.</returns>
    /// <exception cref="System.OperationCanceledException">Analysis was canceled.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public Task AnalyzeAsync(QueryAnalyzerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return AnalyzeCoreAsync(context, cancellationToken);
    }

    internal Task DispatchAsync(QueryAnalyzerContext context, CancellationToken cancellationToken)
        => AnalyzeCoreAsync(context, cancellationToken);

    /// <summary>Analyzes a non-null parsed context with cancellation managed by the caller.</summary>
    /// <param name="context">The non-null parsed statement context.</param>
    /// <param name="cancellationToken">Cancellation requested when the parser's analyzer timeout expires.</param>
    /// <returns>The analyzer's completion task.</returns>
    /// <exception cref="OperationCanceledException">Analysis was canceled.</exception>
    protected abstract Task AnalyzeCoreAsync(QueryAnalyzerContext context, CancellationToken cancellationToken);
}
