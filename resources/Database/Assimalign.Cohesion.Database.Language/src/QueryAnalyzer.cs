using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>An abstract analyzer for implementing custom analysis on parsed queries.</summary>
public abstract class QueryAnalyzer
{
    /// <summary>Analyzes a parsed statement and records diagnostics on it.</summary>
    /// <param name="context">The parsed statement to analyze.</param>
    /// <param name="cancellationToken">Cancellation requested when the parser's analyzer timeout expires.</param>
    /// <returns>A task that completes when analysis and diagnostic reporting finish.</returns>
    /// <exception cref="System.OperationCanceledException">Analysis was canceled.</exception>
    public abstract Task AnalyzeAsync(QueryAnalyzerContext context, CancellationToken cancellationToken = default);
}
